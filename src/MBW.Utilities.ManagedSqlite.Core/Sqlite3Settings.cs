using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;

namespace MBW.Utilities.ManagedSqlite.Core
{
    public class Sqlite3Settings
    {
        /// <summary>
        /// Should the sqlite database have an invalid text encoding, it can be overridden here.
        ///
        /// This is a dangerous setting, as an incorrect encoding will provide garbage data.
        /// </summary>
        public SqliteEncoding? FallbackEncoding { get; set; }
    }
}