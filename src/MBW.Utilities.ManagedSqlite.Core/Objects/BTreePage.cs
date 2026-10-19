using System;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Internal;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;
using MBW.Utilities.ManagedSqlite.Core.Objects.Headers;

namespace MBW.Utilities.ManagedSqlite.Core.Objects;

internal abstract class BTreePage
{
    public uint Page { get; }

    protected internal ReaderBase Reader { get; }
    protected internal BTreeHeader Header { get; }

    protected internal ushort[] CellOffsets { get; }

    protected BTreePage(ReaderBase reader, uint page, BTreeHeader header, ushort[] cellOffsets)
    {
        Reader = reader;
        Page = page;
        Header = header;
        CellOffsets = cellOffsets;
    }

    internal static BTreePage Parse(ReaderBase reader, uint page)
    {
        // Read the page once, and parse it from memory
        byte[] data = reader.ReadPage(page);
        int length = (int)reader.PageSize;

        // Page 1 begins with the 100 byte database header
        int pos = page == 1 ? DatabaseHeader.HeaderSize : 0;

        BTreeHeader header = BTreeHeader.Parse(data, length, ref pos);

        // Read cells. The cell pointer array is in key order, so it must not be re-ordered.
        ushort[] cellOffsets = new ushort[header.CellCount];

        for (ushort i = 0; i < header.CellCount; i++)
        {
            cellOffsets[i] = ByteArrayReader.ReadUInt16(data, length, ref pos);
        }

        BTreePage res;
        switch (header.Type)
        {
            case BTreeType.InteriorIndexBtreePage:
                throw new ArgumentOutOfRangeException();
            case BTreeType.InteriorTableBtreePage:
                res = new BTreeInteriorTablePage(reader, page, header, cellOffsets);
                break;
            case BTreeType.LeafIndexBtreePage:
                throw new ArgumentOutOfRangeException();
            case BTreeType.LeafTableBtreePage:
                res = new BTreeLeafTablePage(reader, page, header, cellOffsets);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        res.ParseInternal(data, length);

        return res;
    }

    protected abstract void ParseInternal(byte[] data, int length);
}