using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests;

public class Schema_Tests
{
    /// <summary>
    /// Indexes, views and triggers are listed with their SQL; only tables are read.
    /// </summary>
    [Fact]
    public void EverySchemaObjectIsListed()
    {
        SqliteParity.AssertSameAsSqlite(@"
CREATE TABLE tbl (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT UNIQUE);
CREATE INDEX tbl_name ON tbl (name DESC);
CREATE VIEW tbl_view AS SELECT name FROM tbl;
CREATE TRIGGER tbl_trigger AFTER INSERT ON tbl BEGIN SELECT 1; END;
INSERT INTO tbl (name) VALUES ('a'), ('b');");
    }
}
