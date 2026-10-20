using System;
using System.Collections.Generic;
using System.IO;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Internal;
using MBW.Utilities.ManagedSqlite.Core.Objects;

namespace MBW.Utilities.ManagedSqlite.Core.Tables;

public class Sqlite3Table
{
    /// <summary>
    /// Payloads larger than this are read into a temporary buffer, so one huge row does not keep a huge buffer alive
    /// </summary>
    private const int MaxReusedBufferSize = 64 * 1024;

    private readonly ReaderBase _reader;

    private BTreePage RootPage { get; }
    public Sqlite3SchemaRow SchemaDefinition { get; }

    internal Sqlite3Table(ReaderBase reader, BTreePage rootPage, Sqlite3SchemaRow table)
    {
        SchemaDefinition = table;
        _reader = reader;
        RootPage = rootPage;
    }

    /// <summary>
    /// Enumerates the table's rows in the order SQLite stores them: rowid order, or primary key order for a WITHOUT
    /// ROWID table. Each row's <see cref="Sqlite3Row.ColumnData"/> is its record as stored; for a WITHOUT ROWID table
    /// that is the primary key columns first, then the others, see <see cref="Sqlite3Row"/>.
    /// </summary>
    public IEnumerable<Sqlite3Row> EnumerateRows()
    {
        // A WITHOUT ROWID table is stored in an index b-tree
        if (RootPage is BTreeIndexPage indexRoot)
            return EnumerateWithoutRowIdRows(indexRoot);

        return EnumerateRowIdRows();
    }

    private IEnumerable<Sqlite3Row> EnumerateRowIdRows()
    {
        byte[] buffer = null;
        long[] serialTypes = null;

        foreach (BTreeLeafTablePage leaf in BTreeTools.WalkTableBTreeLeaves(RootPage))
        {
            BTreeLeafTablePage.Cell[] cells = leaf.Cells;
            for (int i = 0; i < cells.Length; i++)
            {
                BTreeLeafTablePage.Cell cell = cells[i];
                byte[] payload = ReadPayload(leaf, leaf.CellOffsets[i] + (uint)cell.CellHeaderSize, cell.DataSize,
                    cell.DataSizeInCell, cell.FirstOverflowPage, ref buffer);

                yield return new Sqlite3Row(this, cell.RowId, DecodeRecord(payload, (int)cell.DataSize, ref serialTypes));
            }
        }
    }

    private IEnumerable<Sqlite3Row> EnumerateWithoutRowIdRows(BTreeIndexPage root)
    {
        byte[] buffer = null;
        long[] serialTypes = null;

        foreach (KeyValuePair<BTreeIndexPage, int> entry in BTreeTools.WalkIndexBTreeCells(root))
        {
            BTreeIndexPage page = entry.Key;
            BTreeIndexPage.Cell cell = page.Cells[entry.Value];
            byte[] payload = ReadPayload(page, page.CellOffsets[entry.Value] + (uint)cell.CellHeaderSize, cell.DataSize,
                cell.DataSizeInCell, cell.FirstOverflowPage, ref buffer);

            // There is no rowid
            yield return new Sqlite3Row(this, 0, DecodeRecord(payload, (int)cell.DataSize, ref serialTypes));
        }
    }

    /// <summary>
    /// Reads a cell's whole payload once, so it can be decoded from memory. Small payloads go into
    /// <paramref name="buffer"/>, which is grown as needed and reused for the next cell.
    /// </summary>
    private byte[] ReadPayload(BTreePage page, uint dataOffset, long dataSize, uint dataSizeInCell, uint firstOverflowPage, ref byte[] buffer)
    {
        int payloadSize = checked((int)dataSize);
        byte[] payload;
        if (payloadSize <= MaxReusedBufferSize)
        {
            if (buffer == null || buffer.Length < payloadSize)
                buffer = new byte[RoundUpToPowerOfTwo(payloadSize)];

            payload = buffer;
        }
        else
        {
            payload = new byte[payloadSize];
        }

        if (firstOverflowPage == 0)
        {
            // All data is on this page
            _reader.SeekPage(page.Page, dataOffset);
            if (_reader.Read(payload, 0, payloadSize) != payloadSize)
                throw new EndOfStreamException();
        }
        else
        {
            // The data continues on overflow pages
            using SqliteDataStream dataStream = new SqliteDataStream(_reader, page.Page, dataOffset,
                dataSizeInCell, firstOverflowPage, dataSize);

            if (dataStream.ReadFully(payload, 0, payloadSize) != payloadSize)
                throw new EndOfStreamException();
        }

        return payload;
    }

    private static int RoundUpToPowerOfTwo(int value)
    {
        int res = 64;
        while (res < value)
            res <<= 1;

        return res;
    }

    private object[] DecodeRecord(byte[] payload, int length, ref long[] serialTypes)
    {
        serialTypes ??= new long[16];

        int pos = 0;
        long headerSize = ByteArrayReader.ReadVarInt(payload, length, ref pos);
        if (headerSize > length)
            throw new EndOfStreamException();

        int columns = 0;
        while (pos < headerSize)
        {
            if (columns == serialTypes.Length)
                Array.Resize(ref serialTypes, columns * 2);

            serialTypes[columns++] = ByteArrayReader.ReadVarInt(payload, length, ref pos);
        }

        object[] rowData = new object[columns];
        for (int i = 0; i < columns; i++)
        {
            long serialType = serialTypes[i];
            switch (serialType)
            {
                case 0:
                    rowData[i] = null;
                    break;
                case 1:
                case 2:
                case 3:
                case 4:
                    rowData[i] = ByteArrayReader.ReadInteger(payload, length, ref pos, (int)serialType);
                    break;
                case 5:
                    rowData[i] = ByteArrayReader.ReadInteger(payload, length, ref pos, 6);
                    break;
                case 6:
                    rowData[i] = ByteArrayReader.ReadInteger(payload, length, ref pos, 8);
                    break;
                case 7:
                    rowData[i] = BitConverter.Int64BitsToDouble(ByteArrayReader.ReadInteger(payload, length, ref pos, 8));
                    break;
                case 8:
                    // Serial types 8 and 9: integer constants 0 and 1, no payload bytes
                    rowData[i] = 0L;
                    break;
                case 9:
                    rowData[i] = 1L;
                    break;
                case 10:
                case 11:
                    throw new ArgumentOutOfRangeException();
                default:
                {
                    long size = (serialType - 12) / 2;
                    if (size > length - pos)
                        throw new EndOfStreamException();

                    if ((serialType & 0x01) == 0x00)
                    {
                        // Even number
                        byte[] blob = new byte[size];
                        Buffer.BlockCopy(payload, pos, blob, 0, (int)size);
                        rowData[i] = blob;
                    }
                    else
                    {
                        // Odd number
                        rowData[i] = _reader.DecodeString(payload, pos, (int)size);
                    }

                    pos += (int)size;
                    break;
                }
            }
        }

        return rowData;
    }
}
