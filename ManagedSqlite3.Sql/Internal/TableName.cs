namespace ManagedSqlite3.Sql
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