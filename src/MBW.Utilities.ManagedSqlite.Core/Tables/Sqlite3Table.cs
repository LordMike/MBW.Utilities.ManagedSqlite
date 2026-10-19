using System;
using System.Collections.Generic;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Internal;
using MBW.Utilities.ManagedSqlite.Core.Objects;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;

namespace MBW.Utilities.ManagedSqlite.Core.Tables;

public class Sqlite3Table
{
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

        List<ColumnDataMeta> metaInfos = new List<ColumnDataMeta>();
        foreach (BTreeCellData cell in cells)
        {
            metaInfos.Clear();

            // A payload can't be larger than the pages that could hold it, nor larger than SQLite's maximum length
            int usableSize = _reader.PageSize - _reader.ReservedSpace;
            long maxPayload = Math.Min(int.MaxValue, cell.Cell.DataSizeInCell + (long)_reader.PageCount * (usableSize - 4));
            if (cell.Cell.DataSize < 0 || cell.Cell.DataSize > maxPayload)
                throw new SqliteCorruptException($"payload size {cell.Cell.DataSize} of row {cell.Cell.RowId} is larger than the database", cell.Page, "b-tree leaf cell");

            // Create a new stream to cover any fragmentation that might occur
            // The stream is started in the current cells "resident" data, and will overflow to any other pages as needed
            using (SqliteDataStream dataStream = new SqliteDataStream(_reader, cell.Page, cell.CellOffset + cell.Cell.CellHeaderSize,
                       cell.Cell.DataSizeInCell, cell.Cell.FirstOverflowPage, cell.Cell.DataSize))
            {
                ReaderBase reader = new ReaderBase(dataStream, _reader);

                long headerSize = reader.ReadVarInt(out _);
                if (headerSize < 1 || headerSize > cell.Cell.DataSize)
                    throw new SqliteCorruptException($"record header size {headerSize} of row {cell.Cell.RowId} is outside the payload of {cell.Cell.DataSize} bytes", cell.Page, "record header");

                // The largest serial type whose value could still fit in the payload
                long maxSerialType = 2 * cell.Cell.DataSize + 13;

                while (reader.Position < headerSize)
                {
                    long columnInfo = reader.ReadVarInt(out _);
                    if (columnInfo < 0 || columnInfo > maxSerialType)
                        throw new SqliteCorruptException($"serial type {columnInfo} of row {cell.Cell.RowId} does not fit in the payload", cell.Page, "record header");

                    ColumnDataMeta meta = new ColumnDataMeta();
                    if (columnInfo == 0)
                        meta.Type = SqliteDataType.Null;
                    else if (columnInfo == 1)
                    {
                        meta.Type = SqliteDataType.Integer;
                        meta.Length = 1;
                    }
                    else if (columnInfo == 2)
                    {
                        meta.Type = SqliteDataType.Integer;
                        meta.Length = 2;
                    }
                    else if (columnInfo == 3)
                    {
                        meta.Type = SqliteDataType.Integer;
                        meta.Length = 3;
                    }
                    else if (columnInfo == 4)
                    {
                        meta.Type = SqliteDataType.Integer;
                        meta.Length = 4;
                    }
                    else if (columnInfo == 5)
                    {
                        meta.Type = SqliteDataType.Integer;
                        meta.Length = 6;
                    }
                    else if (columnInfo == 6)
                    {
                        meta.Type = SqliteDataType.Integer;
                        meta.Length = 8;
                    }
                    else if (columnInfo == 7)
                    {
                        meta.Type = SqliteDataType.Float;
                        meta.Length = 8;
                    }
                    else if (columnInfo == 8)
                        meta.Type = SqliteDataType.IntegerZero;
                    else if (columnInfo == 9)
                        meta.Type = SqliteDataType.IntegerOne;
                    else if (columnInfo == 10 || columnInfo == 11)
                    {
                        throw new SqliteCorruptException($"serial type {columnInfo} of row {cell.Cell.RowId} is reserved", cell.Page, "record header");
                    }
                    else if ((columnInfo & 0x01) == 0x00)
                    {
                        // Even number
                        meta.Type = SqliteDataType.Blob;
                        meta.Length = (uint)((columnInfo - 12) / 2);
                    }
                    else
                    {
                        // Odd number
                        meta.Type = SqliteDataType.Text;
                        meta.Length = (uint)((columnInfo - 13) / 2);
                    }

                    metaInfos.Add(meta);
                }

                // The header must end exactly where it says, and the values must fill the rest of the payload exactly
                long dataSize = 0;
                foreach (ColumnDataMeta meta in metaInfos)
                    dataSize += meta.Length;

                if (reader.Position != headerSize || headerSize + dataSize != cell.Cell.DataSize)
                    throw new SqliteCorruptException($"record of row {cell.Cell.RowId} has a {reader.Position} byte header (declared {headerSize}) and {dataSize} bytes of values, but the payload is {cell.Cell.DataSize} bytes", cell.Page, "record header");

                object[] rowData = new object[metaInfos.Count];
                for (int i = 0; i < metaInfos.Count; i++)
                {
                    ColumnDataMeta meta = metaInfos[i];
                    switch (meta.Type)
                    {
                        case SqliteDataType.Null:
                            rowData[i] = null;
                            break;
                        case SqliteDataType.Integer:
                            // TODO: Do we handle negatives correctly?
                            rowData[i] = reader.ReadInteger((byte)meta.Length);
                            break;
                        case SqliteDataType.Float:
                            rowData[i] = BitConverter.Int64BitsToDouble(reader.ReadInteger((byte)meta.Length));
                            break;
                        case SqliteDataType.IntegerZero:
                            rowData[i] = 0L;
                            break;
                        case SqliteDataType.IntegerOne:
                            rowData[i] = 1L;
                            break;
                        case SqliteDataType.Blob:
                            rowData[i] = reader.Read((int)meta.Length);
                            break;
                        case SqliteDataType.Text:
                            rowData[i] = reader.ReadString(meta.Length);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }

                yield return new Sqlite3Row(this, cell.Cell.RowId, rowData);
            }
        }
    }
}