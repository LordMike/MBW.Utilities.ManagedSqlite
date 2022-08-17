using System.IO;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;
using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Core.Tests
{
    public class Settings_Tests
    {
        [Fact]
        public void TestInvalidEncoding_DefaultFails()
        {
            Assert.Throws<SqliteInvalidEncodingException>(() =>
            {
                using (Stream _stream = ResourceHelper.OpenResource("MBW.Utilities.ManagedSqlite.Core.Tests.Data.InvalidEncodingHeader.db"))
                using (Sqlite3Database db = new Sqlite3Database(_stream))
                {
                }
            });
        }

        [Fact]
        public void TestInvalidEncoding_SuccessWhenOverridden()
        {
            Sqlite3Settings sqlite3Settings = new Sqlite3Settings
            {
                FallbackEncoding = SqliteEncoding.UTF8
            };

            using (Stream _stream = ResourceHelper.OpenResource("MBW.Utilities.ManagedSqlite.Core.Tests.Data.InvalidEncodingHeader.db"))
            using (Sqlite3Database db = new Sqlite3Database(_stream, sqlite3Settings))
            {
                Assert.Equal(SqliteEncoding.UTF8, db.TextEncoding);
                Assert.Equal((SqliteEncoding)0, db.Header.TextEncoding);
            }
        }
    }
}