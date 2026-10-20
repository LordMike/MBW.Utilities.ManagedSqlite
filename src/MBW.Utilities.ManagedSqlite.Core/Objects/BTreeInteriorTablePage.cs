using System.Diagnostics.CodeAnalysis;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Core.Internal;
using MBW.Utilities.ManagedSqlite.Core.Objects.Headers;

namespace MBW.Utilities.ManagedSqlite.Core.Objects;

/// <summary>
/// SQLite B-Tree datastructure that contains other Interior / Leaf pages
/// </summary>
internal class BTreeInteriorTablePage : BTreePage
{
    public Cell[] Cells { get; private set; }

    public BTreeInteriorTablePage(ReaderBase reader, uint page, BTreeHeader header, ushort[] cellOffsets, byte[] data, int length)
        : base(reader, page, header, cellOffsets)
    {
        ParseCells(data, length);
    }

    [MemberNotNull(nameof(Cells))]
    private void ParseCells(byte[] data, int length)
    {
        Cells = new Cell[CellOffsets.Length];

        for (int i = 0; i < Cells.Length; i++)
        {
            int pos = CellOffsets[i];

            uint leftPagePointer = ByteArrayReader.ReadUInt32(data, length, ref pos);
            long intKey = ByteArrayReader.ReadVarInt(data, length, ref pos);

            Cells[i] = new Cell
            {
                LeftPagePointer = leftPagePointer,
                IntegerKey = intKey
            };
        }
    }

    public struct Cell
    {
        public uint LeftPagePointer;
        public long IntegerKey;
    }
}