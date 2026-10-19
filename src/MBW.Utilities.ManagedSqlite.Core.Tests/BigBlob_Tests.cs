using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests;

public class BigBlob_Tests
{
    /// <summary>
    /// An empty blob, one that fits in the leaf page, and one spread over a chain of overflow pages.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(65535)]
    public void BlobValue(int size)
    {
        SqliteParity.AssertSameAsSqlite($"CREATE TABLE tbl (v BLOB); INSERT INTO tbl VALUES (randomblob({size}));");
    }
}
