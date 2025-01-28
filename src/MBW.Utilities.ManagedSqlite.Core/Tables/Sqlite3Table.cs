using System.Collections.Generic;
using MBW.Utilities.ManagedSqlite.Core.Internal;

namespace MBW.Utilities.ManagedSqlite.Core.Tables;

public class Sqlite3Table
{
    private readonly PagedStream _stream;
    private readonly uint _rootPage;
    private readonly string _name;
    private readonly string _tableName;

    internal Sqlite3Table(PagedStream stream, uint rootPage, string name, string tableName)
    {
        _stream = stream;
        _rootPage = rootPage;
        _name = name;
        _tableName = tableName;
    }

    public IEnumerable<Sqlite3Row> EnumerateRows() => new RowEnumerator(_stream);
}