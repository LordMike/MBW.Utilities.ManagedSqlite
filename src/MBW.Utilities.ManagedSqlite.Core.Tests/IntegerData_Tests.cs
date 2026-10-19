using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests;

public class IntegerData_Tests
{
    /// <summary>
    /// The edges of each integer serial type (0 and 1 have their own, 8 and 9, with no payload), on both signs.
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(2L)]
    [InlineData(-1L)]
    [InlineData(127L)]
    [InlineData(128L)]
    [InlineData(-128L)]
    [InlineData(-129L)]
    [InlineData(32767L)]
    [InlineData(32768L)]
    [InlineData(-32768L)]
    [InlineData(-32769L)]
    [InlineData(8388607L)]
    [InlineData(8388608L)]
    [InlineData(-8388608L)]
    [InlineData(-8388609L)]
    [InlineData(2147483647L)]
    [InlineData(2147483648L)]
    [InlineData(-2147483648L)]
    [InlineData(-2147483649L)]
    [InlineData(140737488355327L)]
    [InlineData(140737488355328L)]
    [InlineData(-140737488355328L)]
    [InlineData(-140737488355329L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void IntegerValue(long value)
    {
        SqliteParity.AssertSameAsSqlite($"CREATE TABLE tbl (v INTEGER); INSERT INTO tbl VALUES ({value});");
    }
}
