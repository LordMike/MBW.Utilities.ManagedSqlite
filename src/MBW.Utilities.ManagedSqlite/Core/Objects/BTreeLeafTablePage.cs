using MBW.Utilities.ManagedSqlite.Core.Internal;
using MBW.Utilities.ManagedSqlite.Core.Objects.Headers;

namespace MBW.Utilities.ManagedSqlite.Core.Objects;

/// <summary>
/// SQLite B-Tree datastructure that cells with data
/// </summary>
internal class BTreeLeafTablePage : BTreePage
{
    public BTreeLeafTablePage(ReaderBase reader, uint page, BTreeHeader header, byte[] data, int cellPointers)
        : base(reader, page, header, data, cellPointers)
    {
    }

    /// <summary>
    /// Parses cell <paramref name="index"/> when it is reached, as SQLite does (btreeParseCellPtr in btree.c)
    /// </summary>
    public PayloadCell GetCell(int index, out long rowId)
    {
        byte[] data = Data;
        int start = GetCellOffset(index);
        int pos = start;

        uint bytes;
        if (start <= (int)Reader.PageSize - MaxCellHeaderSize)
        {
            // Both varints are on the page, so the checks of the reads below cannot fail
            bytes = ReadPayloadSizeInPage(data, ref pos);
            rowId = ReadCellVarIntInPage(data, ref pos);
        }
        else
        {
            bytes = ReadPayloadSize(data, ref pos, start);
            rowId = ReadCellVarInt(data, ref pos, start);
        }

        return new PayloadCell
        {
            PayloadOffset = (uint)pos,
            DataSize = bytes,
            DataSizeInCell = PayloadSizeOnPage(bytes, true)
        };
    }
}
