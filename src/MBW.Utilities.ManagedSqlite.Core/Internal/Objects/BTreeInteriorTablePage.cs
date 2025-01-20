namespace MBW.Utilities.ManagedSqlite.Core.Internal.Objects;

/// <summary>
/// SQLite B-Tree datastructure that contains other Interior / Leaf pages
/// </summary>
internal class BTreeInteriorTablePage
{
    public BTreeInteriorTablePage(ReaderBase reader, uint page, ushort[] cellOffsets)
    {
        Cells = new Cell[cellOffsets.Length];

        for (int i = 0; i < Cells.Length; i++)
        {
            reader.SeekPage(page, cellOffsets[i]);

            uint leftPagePointer = reader.ReadUInt32();
            long intKey = reader.ReadVarInt();

            Cells[i] = new Cell
            {
                LeftPagePointer = leftPagePointer,
                IntegerKey = intKey
            };
        }
    }

    public Cell[] Cells { get; private set; }

    public struct Cell
    {
        public uint LeftPagePointer;
        public long IntegerKey;
    }
}