using System;

namespace MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;

/// <summary>
/// Patches the structures of a copy of an in-memory SQLite database file, to build named corruption cases.
/// See https://www.sqlite.org/fileformat.html for the layout.
/// </summary>
internal sealed class DbPatcher
{
    public byte[] Data { get; private set; }

    /// <summary>
    /// The page size from the unpatched header
    /// </summary>
    public int PageSize { get; }

    public DbPatcher(byte[] data)
    {
        Data = (byte[])data.Clone();
        PageSize = ReadUInt16(16);
    }

    public int PageCount => Data.Length / PageSize;

    public int PageOffset(uint page) => (int)(page - 1) * PageSize;

    /// <summary>
    /// The offset of the b-tree page header, which follows the 100 byte database header on page 1
    /// </summary>
    private int BTreeHeaderOffset(uint page) => PageOffset(page) + (page == 1 ? 100 : 0);

    // Database header

    public DbPatcher SetPageSizeField(ushort value) => WriteUInt16(16, value);

    public DbPatcher SetReservedSpace(byte value) => WriteByte(20, value);

    // B-tree page header

    public DbPatcher SetPageType(uint page, byte type) => WriteByte(BTreeHeaderOffset(page), type);

    public DbPatcher SetCellCount(uint page, ushort count) => WriteUInt16(BTreeHeaderOffset(page) + 3, count);

    public DbPatcher SetRightMostPointer(uint page, uint value) => WriteUInt32(BTreeHeaderOffset(page) + 8, value);

    /// <summary>
    /// The page-relative offset of a cell, from the cell pointer array
    /// </summary>
    private ushort GetCellOffset(uint page, int cell)
    {
        int headerSize = IsInterior(page) ? 12 : 8;
        return ReadUInt16(BTreeHeaderOffset(page) + headerSize + 2 * cell);
    }

    public uint GetChildPointer(uint page, int cell)
    {
        RequireInterior(page);
        return ReadUInt32(PageOffset(page) + GetCellOffset(page, cell));
    }

    public DbPatcher SetChildPointer(uint page, int cell, uint value)
    {
        RequireInterior(page);
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

    public DbPatcher SetOverflowPointer(uint page, int cell, uint value) => WriteUInt32(GetOverflowPointerOffset(page, cell), value);

    // File

    public DbPatcher Truncate(int length)
    {
        Data = Data.AsSpan(0, length).ToArray();
        return this;
    }

    // Primitives

    public DbPatcher WriteByte(int offset, byte value)
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

    public DbPatcher WriteUInt32(int offset, uint value)
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
}
