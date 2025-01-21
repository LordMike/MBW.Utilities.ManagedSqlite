using System;
using System.Collections;
using System.Collections.Generic;
using MBW.Utilities.ManagedSqlite.Core.Internal;
using MBW.Utilities.ManagedSqlite.Core.Internal.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Internal.Objects;
using MBW.Utilities.ManagedSqlite.Core.Internal.Objects.Enums;
using MBW.Utilities.ManagedSqlite.Core.Internal.Objects.Headers;

namespace MBW.Utilities.ManagedSqlite.Core.Tables;

public struct RowEnumerator : IEnumerator<Sqlite3Row>
{
    internal RowEnumerator(PagedStream stream)
    {
        // Read header
        _stream.SeekPage(_rootPage);

        if (_rootPage == 1)
            _stream.Position += DatabaseHeader.HeaderSize;

        CellStream cellStream = new CellStream(_stream);
        BTreeHeader header = new BTreeHeader();
        Stack<uint> pageStack = new Stack<uint>();

        if (header.Type == BTreeType.InteriorTableBtreePage)
        {
            BTreeInteriorTablePage page = new BTreeInteriorTablePage(_rootPage, header.GetCellOffsets());

            foreach (BTreeInteriorTablePage.Cell cell in page.Cells)
            {
                cellStream.SetParameters(page, (ushort)(cell.CellOffset + cell.CellHeaderSize), cell.DataSizeInCell, cell.FirstOverflowPage, cell.DataSize);

                long headerSize = cellStream.ReadVarInt(out _);

                while (cellStream.Position < headerSize)
                {
                    long columnInfo = cellStream.ReadVarInt(out _);

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
                }
                
                // meta.Type switch
                // {
                //     SqliteDataType.Null => null,
                //     SqliteDataType.Integer => reader.ReadInteger((byte)meta.Length), // TODO: Do we handle negatives correctly?
                //     SqliteDataType.Float => BitConverter.Int64BitsToDouble(reader.ReadInteger((byte)meta.Length)),
                //     SqliteDataType.Boolean0 => 0,
                //     SqliteDataType.Boolean1 => 1,
                //     SqliteDataType.Blob => reader.Read((int)meta.Length),
                //     SqliteDataType.Text => reader.ReadString(meta.Length),
                //     _ => throw new ArgumentOutOfRangeException()
                // };

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
    
    public bool MoveNext()
    {
        throw new NotImplementedException();
    }
    public void Reset()
    {
        throw new NotImplementedException();
    }
    public Sqlite3Row Current { get; }

    object? IEnumerator.Current => Current;

    public void Dispose()
    {
        throw new NotImplementedException();
    }
}

public class Sqlite3Table
{
    private readonly PagedStream _stream;
    private readonly uint _rootPage;
    private readonly string _name;
    private readonly string _tableName;

    internal Sqlite3Table(PagedStream stream, uint rootPage, string name, string tableName)
    {
        _stream = stream;
        _rootPage = rootPage;
        _name = name;
        _tableName = tableName;
    }

    public IEnumerable<Sqlite3Row> EnumerateRows() => new RowEnumerator(_stream);
}