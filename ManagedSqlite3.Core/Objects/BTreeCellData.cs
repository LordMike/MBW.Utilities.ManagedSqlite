using System.Diagnostics;

namespace ManagedSqlite3.Core.Objects
{
    [DebuggerDisplay("Page {Page}, Size {Cell.DataSizeInCell} / {Cell.DataSize}")]
    internal class BTreeCellData
    {
        internal uint Page { get; set; }

        internal BTreeLeafTablePage.Cell Cell { get; set; }

        internal ushort CellOffset { get; set; }
    }
}