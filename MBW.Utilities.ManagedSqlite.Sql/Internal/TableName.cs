namespace MBW.Utilities.ManagedSqlite.Sql.Internal
{
    internal struct TableName
    {
        public string Schema;
        public string Name;

        public override string ToString()
        {
            return Schema != null ? Schema + "." + Name : Name;
        }
    }
}