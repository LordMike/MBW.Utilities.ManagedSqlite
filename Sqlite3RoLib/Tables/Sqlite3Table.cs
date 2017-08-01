using System;
using System.Collections.Generic;
using System.IO;
using Sqlite3RoLib.Helpers;
using Sqlite3RoLib.Objects;
using Sqlite3RoLib.Objects.Enums;
using Sqlite3RoLib.Objects.Headers;

namespace Sqlite3RoLib.Tables
{
    public class Sqlite3Table
    {
        private readonly ReaderBase _reader;
        private readonly BTreePage _rootPage;

        internal Sqlite3Table(ReaderBase reader, BTreePage rootPage)
        {
            _reader = reader;
            _rootPage = rootPage;
        }

        public IEnumerable<Sqlite3Row> EnumerateRows()
        {
            IEnumerable<BTreeCellData> cells = BTreeTools.WalkTableBTree(_rootPage);

            List<ColumnDataMeta> metaInfos = new List<ColumnDataMeta>();
            foreach (BTreeCellData cell in cells)
            {
                metaInfos.Clear();

                // TODO: Make a new reader based on a virtual stream of the (potentially) fragmented data
                byte[] data = BTreeTools.ReadCellData(_reader, cell);

                using (MemoryStream ms = new MemoryStream(data))
                {
                    ReaderBase reader = new ReaderBase(ms);
                    reader.ApplySqliteDatabaseHeader(new DatabaseHeader
                    {
                        TextEncoding = _reader.TextEncoding,
                        PageSize = _reader.PageSize,
                        ReservedSpaceAtEndOfPage = _reader.ReservedSpace
                    });

                    byte bytesRead;
                    long headerSize = reader.ReadVarInt(out bytesRead);

                    while (reader.Position < headerSize)
                    {
                        long columnInfo = reader.ReadVarInt(out bytesRead);

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
                            meta.Length = (ushort)((columnInfo - 12) / 2);
                        }
                        else
                        {
                            // Odd number
                            meta.Type = SqliteDataType.Text;
                            meta.Length = (ushort)((columnInfo - 13) / 2);
                        }

                        metaInfos.Add(meta);
                    }

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
                                // TODO: Handle floating points
                                rowData[i] = reader.ReadInteger((byte)meta.Length);
                                break;
                            case SqliteDataType.Boolean0:
                                rowData[i] = false;
                                break;
                            case SqliteDataType.Boolean1:
                                rowData[i] = true;
                                break;
                            case SqliteDataType.Blob:
                                rowData[i] = reader.Read(meta.Length);
                                break;
                            case SqliteDataType.Text:
                                rowData[i] = reader.ReadString(meta.Length);
                                break;
                            default:
                                throw new ArgumentOutOfRangeException();
                        }
                    }

                    yield return new Sqlite3Row(cell.Cell.RowId, rowData);
                }
            }
        }
    }
}