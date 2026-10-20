using System;
using System.Collections.Generic;
using System.IO;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Internal;
using MBW.Utilities.ManagedSqlite.Core.Objects;

namespace MBW.Utilities.ManagedSqlite.Core.Tables;

public sealed class Sqlite3Table
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
    /// Reads every row of the table in rowid order.
    /// </summary>
    /// <exception cref="SqliteCorruptException">The table's b-tree or one of its records is malformed</exception>
    public IEnumerable<Sqlite3Row> EnumerateRows()
    {
        using IEnumerator<Sqlite3Row> rows = EnumerateRowsInternal().GetEnumerator();

        while (true)
        {
            try
            {
                if (!rows.MoveNext())
                    yield break;
            }
            catch (Exception e) when (CorruptionHelper.IsCorruptionShaped(e))
            {
                throw new SqliteCorruptException($"unable to read the rows of table {SchemaDefinition.Name}", structure: "table b-tree", innerException: e);
            }

            yield return rows.Current;
        }
    }

    private IEnumerable<Sqlite3Row> EnumerateRowsInternal()
    {
        // A payload can't be larger than the pages that could hold it, nor larger than SQLite's maximum length
        long usableSize = (long)_reader.PageSize - _reader.ReservedSpace;

        byte[] buffer = null;
        long[] serialTypes = null;

        foreach (BTreeLeafTablePage leaf in BTreeTools.WalkTableBTreeLeaves(RootPage))
        {
            BTreeLeafTablePage.Cell[] cells = leaf.Cells;
            for (int i = 0; i < cells.Length; i++)
            {
                long maxPayload = Math.Min(int.MaxValue, cells[i].DataSizeInCell + _reader.PageCount * (usableSize - 4));
                if (cells[i].DataSize < 0 || cells[i].DataSize > maxPayload)
                    throw new SqliteCorruptException($"payload size {cells[i].DataSize} of row {cells[i].RowId} is larger than the database", leaf.Page, "b-tree leaf cell");

                // Read the whole payload once, then decode it from memory
                int payloadSize = checked((int)cells[i].DataSize);
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

                uint dataOffset = leaf.CellOffsets[i] + (uint)cells[i].CellHeaderSize;
                if (cells[i].FirstOverflowPage == 0)
                {
                    // The payload spills past this page, but there is no overflow chain to hold the rest
                    if (cells[i].DataSizeInCell < cells[i].DataSize)
                        throw new SqliteCorruptException($"the overflow chain of row {cells[i].RowId} ended with {cells[i].DataSize - cells[i].DataSizeInCell} bytes of the payload left", leaf.Page, "overflow chain");

                    // All data is on this page
                    _reader.SeekPage(leaf.Page, dataOffset);
                    if (_reader.Read(payload, 0, payloadSize) != payloadSize)
                        throw new EndOfStreamException();
                }
                else
                {
                    // The data continues on overflow pages
                    using SqliteDataStream dataStream = new SqliteDataStream(_reader, leaf.Page, dataOffset,
                        cells[i].DataSizeInCell, cells[i].FirstOverflowPage, cells[i].DataSize);

                    if (dataStream.ReadFully(payload, 0, payloadSize) != payloadSize)
                        throw new EndOfStreamException();
                }

                yield return new Sqlite3Row(this, cells[i].RowId, DecodeRecord(leaf.Page, cells[i].RowId, payload, payloadSize, ref serialTypes));
            }
        }
    }

    private static int RoundUpToPowerOfTwo(int value)
    {
        int res = 64;
        while (res < value)
            res <<= 1;

        return res;
    }

    private object[] DecodeRecord(uint page, long rowId, byte[] payload, int length, ref long[] serialTypes)
    {
        serialTypes ??= new long[16];

        int pos = 0;
        long headerSize = ByteArrayReader.ReadVarInt(payload, length, ref pos);
        if (headerSize < 1 || headerSize > length)
            throw new SqliteCorruptException($"record header size {headerSize} of row {rowId} is outside the payload of {length} bytes", page, "record header");

        // The largest serial type whose value could still fit in the payload
        long maxSerialType = 2L * length + 13;

        int columns = 0;
        long dataSize = 0;
        while (pos < headerSize)
        {
            if (columns == serialTypes.Length)
                Array.Resize(ref serialTypes, columns * 2);

            long serialType = ByteArrayReader.ReadVarInt(payload, length, ref pos);
            if (serialType < 0 || serialType > maxSerialType)
                throw new SqliteCorruptException($"serial type {serialType} of row {rowId} does not fit in the payload", page, "record header");

            dataSize += SerialTypeSize(serialType);
            serialTypes[columns++] = serialType;
        }

        // The header must end exactly where it says, and the values must fill the rest of the payload exactly
        if (pos != headerSize || headerSize + dataSize != length)
            throw new SqliteCorruptException($"record of row {rowId} has a {pos} byte header (declared {headerSize}) and {dataSize} bytes of values, but the payload is {length} bytes", page, "record header");

        object[] rowData = new object[columns];
        for (int i = 0; i < columns; i++)
        {
            long serialType = serialTypes[i];
            switch (serialType)
            {
                case 0:
                case 10:
                case 11:
                    // 10 and 11 are reserved. SQLite reads them as NULL with no payload bytes, and so does this.
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

    private static long SerialTypeSize(long serialType)
    {
        switch (serialType)
        {
            case 0:
            case 8:
            case 9:
            case 10:
            case 11:
                return 0;
            case 1:
            case 2:
            case 3:
            case 4:
                return serialType;
            case 5:
                return 6;
            case 6:
            case 7:
                return 8;
            default:
                return (serialType - 12) / 2;
        }
    }
}
