using Sqlite3RoLib.Objects.Headers;

namespace Sqlite3RoLib.Objects
{
    /// <summary>
    /// SQLite B-Tree datastructure that contains other Interior / Leaf pages
    /// </summary>
    internal class BTreeInteriorTablePage : BTreePage
    {
        public Cell[] Cells { get; private set; }

        public BTreeInteriorTablePage(ReaderBase reader, uint page, BTreeHeader header, ushort[] cellOffsets)
            : base(reader, page, header, cellOffsets)
        {

        }
        
        protected override void ParseInternal()
        {
            Cells = new Cell[CellOffsets.Length];

            for (int i = 0; i < Cells.Length; i++)
            {
                Reader.SeekPage(Page, CellOffsets[i]);

                uint leftPagePointer = Reader.ReadUInt32();
                long intKey = Reader.ReadVarInt();

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
}