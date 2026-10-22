using System;
using System.Collections.Generic;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;

/// <summary>
/// Patches the structures of a copy of an in-memory SQLite database file, to build named corruption cases.
/// See https://www.sqlite.org/fileformat.html for the layout. Every write first asserts that it lands where it means
/// to: a raw byte holds the value the caller expects, a header field holds a value SQLite writes, and a b-tree field is
/// on a b-tree page and holds a valid value, so a patch cannot drift onto other bytes unnoticed.
/// </summary>
internal sealed class DbPatcher
{
    private byte[] _data;

    public byte[] Data => _data;

    /// <summary>
    /// The page size from the unpatched header
    /// </summary>
    public int PageSize { get; }

    public DbPatcher(byte[] data)
    {
        _data = (byte[])data.Clone();
        PageSize = ReadUInt16(16);
    }

    public int PageCount => Data.Length / PageSize;

    public int PageOffset(uint page) => (int)(page - 1) * PageSize;

    /// <summary>
    /// The offset of the b-tree page header, which follows the 100 byte database header on page 1
    /// </summary>
    private int BTreeHeaderOffset(uint page) => PageOffset(page) + (page == 1 ? 100 : 0);

    // Database header

    public DbPatcher SetPageSizeField(ushort value)
    {
        Assert.Equal(PageSize, ReadUInt16(16));
        return WriteUInt16(16, value);
    }

    public DbPatcher SetReadVersion(byte value)
    {
        Assert.InRange(Data[19], 1, 2);
        return Write(19, value);
    }

    public DbPatcher SetReservedSpace(byte value)
    {
        Assert.True(PageSize - Data[20] >= 480);
        return Write(20, value);
    }

    public DbPatcher SetMaxEmbeddedPayloadFraction(byte value) => WriteByte(21, 64, value);

    /// <summary>
    /// The in-header database size at offset 28. The base database's version-valid-for number equals its change
    /// counter, so the reader trusts this field.
    /// </summary>
    public DbPatcher SetDatabaseSizeInPages(uint value)
    {
        Assert.Equal((uint)PageCount, ReadUInt32(28));
        return WriteUInt32(28, value);
    }

    /// <summary>
    /// The version-valid-for number at offset 92. When it differs from the change counter, the in-header database size
    /// is ignored.
    /// </summary>
    public DbPatcher SetVersionValidFor(uint value)
    {
        Assert.Equal(ReadUInt32(24), ReadUInt32(92));
        return WriteUInt32(92, value);
    }

    // B-tree page header

    public DbPatcher SetPageType(uint page, byte type)
    {
        RequireBTreePage(page);
        return Write(BTreeHeaderOffset(page), type);
    }

    public DbPatcher SetCellCount(uint page, ushort count)
    {
        RequireBTreePage(page);
        Assert.InRange(ReadUInt16(BTreeHeaderOffset(page) + 3), 0, (PageSize - 8) / 6);
        return WriteUInt16(BTreeHeaderOffset(page) + 3, count);
    }

    public DbPatcher SetRightMostPointer(uint page, uint value)
    {
        RequireInterior(page);
        RequirePageNumber(ReadUInt32(BTreeHeaderOffset(page) + 8));
        return WriteUInt32(BTreeHeaderOffset(page) + 8, value);
    }

    /// <summary>
    /// The page-relative offset of a cell, from the cell pointer array
    /// </summary>
    private ushort GetCellOffset(uint page, int cell)
    {
        int headerSize = IsInterior(page) ? 12 : 8;
        return ReadUInt16(BTreeHeaderOffset(page) + headerSize + 2 * cell);
    }

    public DbPatcher SetCellOffset(uint page, int cell, ushort offset)
    {
        RequireBTreePage(page);
        Assert.InRange(GetCellOffset(page, cell), 8, PageSize - 1);
        int headerSize = IsInterior(page) ? 12 : 8;
        return WriteUInt16(BTreeHeaderOffset(page) + headerSize + 2 * cell, offset);
    }

    public uint GetChildPointer(uint page, int cell)
    {
        RequireInterior(page);
        return ReadUInt32(PageOffset(page) + GetCellOffset(page, cell));
    }

    public DbPatcher SetChildPointer(uint page, int cell, uint value)
    {
        RequireInterior(page);
        RequirePageNumber(GetChildPointer(page, cell));
        return WriteUInt32(PageOffset(page) + GetCellOffset(page, cell), value);
    }

    // Leaf cells and records

    /// <summary>
    /// The file offset of a leaf cell's record (its record header size varint), and the payload size. A leaf table
    /// cell has a rowid between the two, a leaf index cell does not.
    /// </summary>
    public int GetRecordOffset(uint page, int cell, out long payloadSize)
    {
        byte type = Data[BTreeHeaderOffset(page)];
        if (type != 0x0D && type != 0x0A)
            throw new InvalidOperationException($"Page {page} is not a leaf page");

        int pos = PageOffset(page) + GetCellOffset(page, cell);
        payloadSize = ReadVarInt(ref pos);
        if (type == 0x0D)
            ReadVarInt(ref pos); // rowid

        return pos;
    }

    /// <summary>
    /// The file offset of a leaf cell's first overflow page pointer
    /// </summary>
    private int GetOverflowPointerOffset(uint page, int cell)
    {
        int recordOffset = GetRecordOffset(page, cell, out long p);

        // https://www.sqlite.org/fileformat.html#cell_payload, table leaf cells
        int u = PageSize - Data[20];
        int x = u - 35;
        int m = (u - 12) * 32 / 255 - 23;
        int k = (int)(m + (p - m) % (u - 4));

        if (p <= x)
            throw new InvalidOperationException($"Cell {cell} on page {page} does not overflow");

        return recordOffset + (k <= x ? k : m);
    }

    public uint GetOverflowPointer(uint page, int cell) => ReadUInt32(GetOverflowPointerOffset(page, cell));

    public DbPatcher SetOverflowPointer(uint page, int cell, uint value)
    {
        RequirePageNumber(GetOverflowPointer(page, cell));
        return WriteUInt32(GetOverflowPointerOffset(page, cell), value);
    }

    /// <summary>
    /// The pages of a cell's overflow chain, in order
    /// </summary>
    public List<uint> GetOverflowChain(uint page, int cell)
    {
        List<uint> chain = new List<uint>();
        for (uint next = GetOverflowPointer(page, cell); next != 0; next = ReadUInt32(PageOffset(next)))
            chain.Add(next);

        return chain;
    }

    /// <summary>
    /// Sets the next pointer at the start of an overflow page
    /// </summary>
    public DbPatcher SetOverflowNextPointer(uint overflowPage, uint value)
    {
        uint next = ReadUInt32(PageOffset(overflowPage));
        if (next != 0)
            RequirePageNumber(next);
        return WriteUInt32(PageOffset(overflowPage), value);
    }

    // File

    public DbPatcher Truncate(int length)
    {
        _data = Data.AsSpan(0, length).ToArray();
        return this;
    }

    /// <summary>
    /// Appends zeroed pages, which the header's database size does not count
    /// </summary>
    public DbPatcher AppendPages(int count)
    {
        Array.Resize(ref _data, Data.Length + count * PageSize);
        return this;
    }

    // Primitives

    /// <summary>
    /// Overwrites the byte at <paramref name="offset"/>, which must hold <paramref name="expected"/>
    /// </summary>
    public DbPatcher WriteByte(int offset, byte expected, byte value)
    {
        Assert.Equal(expected, Data[offset]);
        return Write(offset, value);
    }

    private DbPatcher Write(int offset, byte value)
    {
        Data[offset] = value;
        return this;
    }

    private DbPatcher WriteUInt16(int offset, ushort value)
    {
        Data[offset] = (byte)(value >> 8);
        Data[offset + 1] = (byte)value;
        return this;
    }

    private DbPatcher WriteUInt32(int offset, uint value)
    {
        Data[offset] = (byte)(value >> 24);
        Data[offset + 1] = (byte)(value >> 16);
        Data[offset + 2] = (byte)(value >> 8);
        Data[offset + 3] = (byte)value;
        return this;
    }

    private ushort ReadUInt16(int offset) => (ushort)((Data[offset] << 8) | Data[offset + 1]);

    public uint ReadUInt32(int offset) => ((uint)Data[offset] << 24) | ((uint)Data[offset + 1] << 16) | ((uint)Data[offset + 2] << 8) | Data[offset + 3];

    private long ReadVarInt(ref int pos)
    {
        long res = 0;
        for (int i = 0; i < 8; i++)
        {
            byte b = Data[pos++];
            res = (res << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
                return res;
        }

        return (res << 8) | Data[pos++];
    }

    private bool IsInterior(uint page) => Data[BTreeHeaderOffset(page)] == 0x05 || Data[BTreeHeaderOffset(page)] == 0x02;

    private void RequireInterior(uint page)
    {
        if (!IsInterior(page))
            throw new InvalidOperationException($"Page {page} is not an interior page");
    }

    private void RequireBTreePage(uint page) => Assert.Contains(Data[BTreeHeaderOffset(page)], new byte[] { 0x02, 0x05, 0x0A, 0x0D });

    private void RequirePageNumber(uint page) => Assert.InRange(page, 2u, (uint)PageCount);
}
