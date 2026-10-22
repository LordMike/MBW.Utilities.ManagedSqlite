## MBW.Utilities.ManagedSqlite [![Generic Build](https://github.com/LordMike/MBW.Utilities.ManagedSqlite/actions/workflows/dotnet.yml/badge.svg)](https://github.com/LordMike/MBW.Utilities.ManagedSqlite/actions/workflows/dotnet.yml) [![NuGet](https://img.shields.io/nuget/v/MBW.Utilities.ManagedSqlite.svg)](https://www.nuget.org/packages/MBW.Utilities.ManagedSqlite) [![GHPackages](https://img.shields.io/badge/package-alpha-green)](https://github.com/LordMike/MBW.Utilities.ManagedSqlite/packages/692005)

Managed C# read-only parser for SQLite3 databases. Provides access to table and row data.

## Packages

| Package | Nuget |
| ------------- |:-------------:|
| MBW.Utilities.ManagedSqlite | [![NuGet](https://img.shields.io/nuget/v/MBW.Utilities.ManagedSqlite.svg)](https://www.nuget.org/packages/MBW.Utilities.ManagedSqlite) |

`MBW.Utilities.ManagedSqlite.Core` and `MBW.Utilities.ManagedSqlite.Sql` are now part of this package. Their packages
only forward their types to it, so code built against them keeps working; reference `MBW.Utilities.ManagedSqlite`
instead. The namespaces are unchanged. Update `Core` and `Sql` to the release that forwards, or remove them, when you add
`MBW.Utilities.ManagedSqlite`: an older version beside it defines the same types twice.

## How to use

```csharp
using (Stream fs = File.OpenRead("MyDb.db"))
using (Sqlite3Database db = new Sqlite3Database(fs))
{
    // Access all tables using db.GetTables(), or a single using db.GetTable("name")
    Sqlite3Table tbl = db.GetTable("MyTable");
    
    // Read out the SQL schema of the table (to get column names)
    SqlTableDefinition schema = tbl.GetTableDefinition();

    // Iterate all rows on the table using EnumerateRows()
    // Note that this library does not do SQL selects, it's a raw parser
    IEnumerable<Sqlite3Row> rows = tbl.EnumerateRows();
    
    // Get the data of column 0 on row 0
    Sqlite3Row row = rows.First();
    object data = row.ColumnData[0];

    // TryGetValueByName() reads a column by name, and also detects row-id substitutes
    row.TryGetValueByName("Id", out var myId); // This actually reads the row id behind the scenes, if Id is an Integer Primary Key
    
    // Try casting it to a specific type (you must know the C# type)
    row.TryGetOrdinal<int>(0, out int myInteger);
}
```

### Features

* Reads SQLite3 databases, in all managed code
* Provides close-to-raw access to the structured data
* Parses the SQLite3 schemas (the CREATE statements) with a port of SQLite's own parser

## Notes

* This is not an ADO.Net substitute, it will not support SQL statements or use indexes - it will only provide access to the data in a database.
* All the SQL parsing logic is basic, it will work in most cases. You're welcome to present cases where it does not work.
* Interestingly, SQLite3 allows _any_ value in _any_ column. So you may get different types out of the raw files, should you encounter an app that does this.

## Known limitations

The goal is to read a database file exactly as SQLite itself reads it. These are the known places where it does not, or where it deliberately does less.

### Out of scope

* **Read-only.** There is no way to write, update or delete data.
* **No SQL execution.** Queries, views, triggers and expressions are not run. Rows are read in the order they are stored (rowid order, or primary key order for WITHOUT ROWID tables), and indexes are listed but never used.
* **Virtual tables cannot be read.** Their rows are produced by a SQLite extension (such as fts5 or rtree) at query time, not stored in the file, so `GetTable()` throws `NotSupportedException`. Their shadow tables (for fts5: `<name>_content`, `<name>_data` and so on) are ordinary tables and can be read.
* **VIRTUAL generated columns have no value.** SQLite computes them from their expression each time they are read, and nothing is stored in the file. STORED generated columns are stored and read normally.
* **Only the main database file is read.** Changes still in a `-wal` file (WAL mode) or a hot `-journal` are not seen. Checkpoint the database, or open it with SQLite once, before reading it here.
* **No locking.** Reading a file while another process writes to it can return inconsistent data.

### Differences from SQLite

* **Column DEFAULTs are applied by name only.** A row written before `ALTER TABLE ... ADD COLUMN` does not store the new column, and SQLite reads the column's DEFAULT for it. `TryGetValueByName` does the same; `TryGetOrdinal` and `ColumnData` show the record as stored, without the column. A DEFAULT with a `CAST`, or a negation that is a real in a TEXT column, is not evaluated, and reads as no value. A column without a DEFAULT reads as NULL, as in SQLite.
* **The whole schema is checked when the first table definition is asked for**, not when the database is opened. In Strict, a malformed table, view, index or trigger then makes every `GetTableDefinition` throw `SqliteCorruptException`, as SQLite reads no table from such a database. Lenient checks each table's own SQL only.
* **No recovery from corruption.** Reading a damaged table throws, and there is no mode that skips the damaged part and continues.

## SQLite3 Docs
* Format changes: https://www.sqlite.org/formatchng.html
* File format: https://www.sqlite.org/fileformat.html

## Data types mapping

SQLite has five storage classes, and a column's declared type only sets its [affinity](https://www.sqlite.org/datatype3.html#determination_of_column_affinity). `SqlTableColumn.DetectedType` follows SQLite's rules, checked in this order against the declared type name (case-insensitive, matching anywhere in the name):

| Declared type contains | Affinity | `DetectedType` | Examples |
|---|---|---|---|
| `INT` | INTEGER | `long` | `INT`, `INTEGER`, `BIGINT`, `TINYINT`, `INT8`, `POINT` |
| `CHAR`, `CLOB` or `TEXT` | TEXT | `string` | `TEXT`, `VARCHAR(255)`, `NCHAR`, `CLOB` |
| `BLOB`, or no type at all | BLOB | `byte[]` | `BLOB`, a column with no type |
| `REAL`, `FLOA` or `DOUB` | REAL | `double` | `REAL`, `FLOAT`, `DOUBLE PRECISION`, `FLOAT8` |
| anything else | NUMERIC | `long` | `NUMERIC`, `DECIMAL(10,5)`, `BOOLEAN`, `DATE`, `DATETIME`, `GUID` |

Because the rules are checked in order, `FLOATING POINT` contains `INT` and gets INTEGER affinity, as in SQLite. A size such as `VARCHAR(255)` is ignored. `SqlTableColumn.TypeName` is the declared type's words, each unquoted and without the size: `double precision` for `double precision`, `VARCHAR` for `VARCHAR(255)`, and an empty string for a column with no type.

SQLite allows any value in any column, so a row can still hold a value of a different type than `DetectedType`. Values are returned as they are stored, except that reading a column by name returns a whole-number value in a REAL column as a `double`, as SQLite does.
