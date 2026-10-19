using System;
using System.Collections.Generic;
using System.IO;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
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
        IEnumerable<BTreeCellData> cells = BTreeTools.WalkTableBTree(RootPage);

        byte[] buffer = null;
        long[] serialTypes = null;

        foreach (BTreeCellData cell in cells)
        {
            // A payload can't be larger than the pages that could hold it, nor larger than SQLite's maximum length
            long usableSize = (long)_reader.PageSize - _reader.ReservedSpace;
            long maxPayload = Math.Min(int.MaxValue, cell.Cell.DataSizeInCell + _reader.PageCount * (usableSize - 4));
            if (cell.Cell.DataSize < 0 || cell.Cell.DataSize > maxPayload)
                throw new SqliteCorruptException($"payload size {cell.Cell.DataSize} of row {cell.Cell.RowId} is larger than the database", cell.Page, "b-tree leaf cell");

            // Read the whole payload once, then decode it from memory
            int payloadSize = checked((int)cell.Cell.DataSize);
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

            uint dataOffset = cell.CellOffset + cell.Cell.CellHeaderSize;
            if (cell.Cell.FirstOverflowPage == 0)
            {
                // The payload spills past this page, but there is no overflow chain to hold the rest
                if (cell.Cell.DataSizeInCell < cell.Cell.DataSize)
                    throw new SqliteCorruptException($"the overflow chain of row {cell.Cell.RowId} ended with {cell.Cell.DataSize - cell.Cell.DataSizeInCell} bytes of the payload left", cell.Page, "overflow chain");

                // All data is on this page
                _reader.SeekPage(cell.Page, dataOffset);
                if (_reader.Read(payload, 0, payloadSize) != payloadSize)
                    throw new EndOfStreamException();
            }
            else
            {
                // The data continues on overflow pages
                using SqliteDataStream dataStream = new SqliteDataStream(_reader, cell.Page, dataOffset,
                    cell.Cell.DataSizeInCell, cell.Cell.FirstOverflowPage, cell.Cell.DataSize);

                if (dataStream.ReadFully(payload, 0, payloadSize) != payloadSize)
                    throw new EndOfStreamException();
            }

            yield return new Sqlite3Row(this, cell.Cell.RowId, DecodeRecord(cell, payload, payloadSize, ref serialTypes));
        }
    }

    private static int RoundUpToPowerOfTwo(int value)
    {
        int res = 64;
        while (res < value)
            res <<= 1;

        return res;
    }

    private object[] DecodeRecord(BTreeCellData cell, byte[] payload, int length, ref long[] serialTypes)
    {
        serialTypes ??= new long[16];

        int pos = 0;
        long headerSize = ReadVarInt(payload, length, ref pos);
        if (headerSize < 1 || headerSize > length)
            throw new SqliteCorruptException($"record header size {headerSize} of row {cell.Cell.RowId} is outside the payload of {length} bytes", cell.Page, "record header");

        // The largest serial type whose value could still fit in the payload
        long maxSerialType = 2L * length + 13;

        int columns = 0;
        long dataSize = 0;
        while (pos < headerSize)
        {
            if (columns == serialTypes.Length)
                Array.Resize(ref serialTypes, columns * 2);

            long serialType = ReadVarInt(payload, length, ref pos);
            if (serialType < 0 || serialType > maxSerialType)
                throw new SqliteCorruptException($"serial type {serialType} of row {cell.Cell.RowId} does not fit in the payload", cell.Page, "record header");
            if (serialType == 10 || serialType == 11)
                throw new SqliteCorruptException($"serial type {serialType} of row {cell.Cell.RowId} is reserved", cell.Page, "record header");

            dataSize += SerialTypeSize(serialType);
            serialTypes[columns++] = serialType;
        }

        // The header must end exactly where it says, and the values must fill the rest of the payload exactly
        if (pos != headerSize || headerSize + dataSize != length)
            throw new SqliteCorruptException($"record of row {cell.Cell.RowId} has a {pos} byte header (declared {headerSize}) and {dataSize} bytes of values, but the payload is {length} bytes", cell.Page, "record header");

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
                    rowData[i] = ReadInteger(payload, length, ref pos, (int)serialType);
                    break;
                case 5:
                    rowData[i] = ReadInteger(payload, length, ref pos, 6);
                    break;
                case 6:
                    rowData[i] = ReadInteger(payload, length, ref pos, 8);
                    break;
                case 7:
                    rowData[i] = BitConverter.Int64BitsToDouble(ReadInteger(payload, length, ref pos, 8));
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

    private static long SerialTypeSize(long serialType)
    {
        switch (serialType)
        {
            case 0:
            case 8:
            case 9:
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

    private static long ReadVarInt(byte[] data, int length, ref int pos)
    {
        // See ReaderBase.ReadVarInt: 8 bytes of 7 bits each, then a 9th byte of 8 bits
        long res = 0;
        for (int i = 0; i < 8; i++)
        {
            if (pos >= length)
                throw new EndOfStreamException();

            byte tmp = data[pos++];
            res = (res << 7) | (long)(tmp & 0x7F);

            if ((tmp & 0x80) == 0x00)
                return res;
        }

        if (pos >= length)
            throw new EndOfStreamException();

        return (res << 8) | data[pos++];
    }

    private static long ReadInteger(byte[] data, int length, ref int pos, int bytes)
    {
        if (bytes > length - pos)
            throw new EndOfStreamException();

        // Big-endian two's complement; the sign comes from the first byte
        long res = (sbyte)data[pos++];
        for (int i = 1; i < bytes; i++)
            res = (res << 8) | data[pos++];

        return res;
    }
}
