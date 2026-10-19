using System;

namespace MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;

/// <summary>
/// Patches the structures of an in-memory SQLite database file, to build named corruption cases.
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
    public int BTreeHeaderOffset(uint page) => PageOffset(page) + (page == 1 ? 100 : 0);

    // Database header

    public DbPatcher SetPageSizeField(ushort value) => WriteUInt16(16, value);

    public DbPatcher SetReservedSpace(byte value) => WriteByte(20, value);

    public DbPatcher SetMagicByte(int index, byte value) => WriteByte(index, value);

    // B-tree page header

    public byte GetPageType(uint page) => Data[BTreeHeaderOffset(page)];

    public DbPatcher SetPageType(uint page, byte type) => WriteByte(BTreeHeaderOffset(page), type);

    public ushort GetCellCount(uint page) => ReadUInt16(BTreeHeaderOffset(page) + 3);

    public DbPatcher SetCellCount(uint page, ushort count) => WriteUInt16(BTreeHeaderOffset(page) + 3, count);

    public uint GetRightMostPointer(uint page)
    {
        RequireInterior(page);
        return ReadUInt32(BTreeHeaderOffset(page) + 8);
    }

    public DbPatcher SetRightMostPointer(uint page, uint value)
    {
        // Writing the field is allowed on any page, so a leaf can be turned into an interior page
        return WriteUInt32(BTreeHeaderOffset(page) + 8, value);
    }

    /// <summary>
    /// Turns a page into an interior table page without cells, whose only child is <paramref name="rightMostPointer"/>
    /// </summary>
    public DbPatcher MakeEmptyInterior(uint page, uint rightMostPointer)
    {
        SetPageType(page, 0x05);
        SetCellCount(page, 0);
        return SetRightMostPointer(page, rightMostPointer);
    }

    /// <summary>
    /// The page-relative offset of a cell, from the cell pointer array
    /// </summary>
    public ushort GetCellOffset(uint page, int cell)
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

    // Leaf table cells and records

    /// <summary>
    /// The file offset of a leaf cell's record (its record header size varint), and the payload size
    /// </summary>
    public int GetRecordOffset(uint page, int cell, out long payloadSize)
    {
        if (GetPageType(page) != 0x0D)
            throw new InvalidOperationException($"Page {page} is not a leaf table page");

        int pos = PageOffset(page) + GetCellOffset(page, cell);
        payloadSize = ReadVarInt(ref pos);
        ReadVarInt(ref pos); // rowid

        return pos;
    }

    /// <summary>
    /// The file offset of a leaf cell's first overflow page pointer
    /// </summary>
    public int GetOverflowPointerOffset(uint page, int cell)
    {
        int recordOffset = GetRecordOffset(page, cell, out long p);

        // https://www.sqlite.org/fileformat.html#cell_payload, table leaf cells
        int u = PageSize - Data[20];
        int x = u - 35;
        int m = (u - 12) * 32 / 255 - 23;
        int k = (int)(m + (p - m) % (u - 4));

        if (p <= x)
            throw new InvalidOperationException($"Cell {cell} on page {page} does not overflow");

        int local = k <= x ? k : m;
        return recordOffset + local;
    }

    public uint GetOverflowPointer(uint page, int cell) => ReadUInt32(GetOverflowPointerOffset(page, cell));

    public DbPatcher SetOverflowPointer(uint page, int cell, uint value) => WriteUInt32(GetOverflowPointerOffset(page, cell), value);

    // File

    public DbPatcher TruncateToPages(int pages)
    {
        byte[] res = new byte[pages * PageSize];
        Array.Copy(Data, res, res.Length);
        Data = res;

        return this;
    }

    // Primitives

    public DbPatcher WriteByte(int offset, byte value)
    {
        Data[offset] = value;
        return this;
    }

    public DbPatcher WriteUInt16(int offset, ushort value)
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

    private uint ReadUInt32(int offset) => ((uint)Data[offset] << 24) | ((uint)Data[offset + 1] << 16) | ((uint)Data[offset + 2] << 8) | Data[offset + 3];

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

    private bool IsInterior(uint page) => GetPageType(page) == 0x05 || GetPageType(page) == 0x02;

    private void RequireInterior(uint page)
    {
        if (!IsInterior(page))
            throw new InvalidOperationException($"Page {page} is not an interior page");
    }
}
