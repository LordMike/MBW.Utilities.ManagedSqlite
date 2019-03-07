namespace MBW.Utilities.ManagedSqlite.Core.Tables
{
    public class Sqlite3SchemaRow
    {
        public Sqlite3Database Database { get; internal set; }
        public string Type { get; internal set; }

        public string Name { get; internal set; }

        public string TableName { get; internal set; }

        public uint RootPage { get; internal set; }

        public string Sql { get; internal set; }
    }
}