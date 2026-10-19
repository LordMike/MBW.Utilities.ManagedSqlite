using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests;

public class AlterTable_Tests
{
    /// <summary>
    /// Rows written before ADD COLUMN have no value for the new column in their record, and read as NULL.
    /// </summary>
    [Fact]
    public void AddedColumnIsNullInOlderRows()
    {
        SqliteParity.AssertSameAsSqlite(@"
CREATE TABLE tbl (Id INTEGER PRIMARY KEY, MyInt INTEGER);
INSERT INTO tbl VALUES (0, 0), (1, 500), (2, 1000);
ALTER TABLE tbl ADD COLUMN NewColumn INTEGER;
UPDATE tbl SET NewColumn = 500 WHERE Id = 1;
INSERT INTO tbl VALUES (3, 1500, 3000);");
    }
}
