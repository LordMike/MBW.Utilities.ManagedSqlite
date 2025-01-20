using System;
using System.Collections.Generic;
using MBW.Utilities.ManagedSqlite.Core.Internal;
using MBW.Utilities.ManagedSqlite.Core.Internal.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Internal.Objects;
using MBW.Utilities.ManagedSqlite.Core.Internal.Objects.Enums;
using MBW.Utilities.ManagedSqlite.Core.Internal.Objects.Headers;

namespace MBW.Utilities.ManagedSqlite.Core.Tables;

public class Sqlite3Table
{
    private readonly ReaderBase _reader;
    private readonly uint _rootPage;
    private readonly string _name;
    private readonly string _tableName;

    internal Sqlite3Table(ReaderBase reader, uint rootPage, string name, string tableName)
    {
        _reader = reader;
        _rootPage = rootPage;
        _name = name;
        _tableName = tableName;
    }

    public IEnumerable<Sqlite3Row> EnumerateRows()
    {
        // Read header
        _reader.SeekPage(_rootPage);

        if (_rootPage == 1)
            _reader.Skip(DatabaseHeader.HeaderSize); // Skip the first 100 bytes

        BTreeHeader header = BTreeHeader.Parse(_reader);

        // Read cells
        ushort[] cellOffsets = new ushort[header.CellCount];

        for (ushort i = 0; i < header.CellCount; i++)
            cellOffsets[i] = _reader.ReadUInt16();

        //TODO: needed?
        Array.Sort(cellOffsets);

        List<ColumnDataMeta> metaInfos = new List<ColumnDataMeta>();

        if (header.Type == BTreeType.InteriorTableBtreePage)
        {
            BTreeInteriorTablePage page = new BTreeInteriorTablePage(_reader, _rootPage, cellOffsets);

            foreach (BTreeInteriorTablePage.Cell cell in page.Cells)
            {
                metaInfos.Clear();

                SqliteDataStream dataStream = new SqliteDataStream(_reader, cell.Page, (ushort)(cell.CellOffset + cell.Cell.CellHeaderSize), cell.Cell.DataSizeInCell, cell.Cell.FirstOverflowPage, cell.Cell.DataSize);
                ReaderBase reader = new ReaderBase(dataStream);

                long headerSize = reader.ReadVarInt(out _);

                while (reader.Position < headerSize)
                {
                    long columnInfo = reader.ReadVarInt(out _);

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
                        meta.Type = SqliteDataType.Boolean0;
                    else if (columnInfo == 9)
                        meta.Type = SqliteDataType.Boolean1;
                    else if (columnInfo == 10 || columnInfo == 11)
                    {
                        throw new ArgumentOutOfRangeException();
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

                object?[] rowData = new object?[metaInfos.Count];
                for (int i = 0; i < metaInfos.Count; i++)
                {
                    ColumnDataMeta meta = metaInfos[i];
                    rowData[i] = meta.Type switch
                    {
                        SqliteDataType.Null => null,
                        SqliteDataType.Integer => reader.ReadInteger((byte)meta.Length), // TODO: Do we handle negatives correctly?
                        SqliteDataType.Float => BitConverter.Int64BitsToDouble(reader.ReadInteger((byte)meta.Length)),
                        SqliteDataType.Boolean0 => 0,
                        SqliteDataType.Boolean1 => 1,
                        SqliteDataType.Blob => reader.Read((int)meta.Length),
                        SqliteDataType.Text => reader.ReadString(meta.Length),
                        _ => throw new ArgumentOutOfRangeException()
                    };
                }

                yield return new Sqlite3Row(this, cell.RowId, rowData);
            }
        }
        else if (header.Type == BTreeType.LeafTableBtreePage)
        {
            var page = new BTreeLeafTablePage(_reader, _rootPage, cellOffsets);
        }
        else
        {
            throw new InvalidOperationException("Invalid page type");
        }
    }
}