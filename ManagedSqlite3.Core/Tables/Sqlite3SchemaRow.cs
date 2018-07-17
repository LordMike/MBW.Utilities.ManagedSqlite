namespace ManagedSqlite3.Core.Tables
{
    public class Sqlite3SchemaRow
    {
        public string Type { get; set; }

        public string Name { get; set; }

        public string TableName { get; set; }

        public uint RootPage { get; set; }

        public string Sql { get; set; }
    }
}