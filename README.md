## MBW.Utilities.ManagedSqlite [![Generic Build](https://github.com/LordMike/MBW.Utilities.ManagedSqlite/actions/workflows/ci.yml/badge.svg)](https://github.com/LordMike/MBW.Utilities.ManagedSqlite/actions/workflows/ci.yml) [![NuGet](https://img.shields.io/nuget/v/MBW.Utilities.ManagedSqlite.svg)](https://www.nuget.org/packages/MBW.Utilities.ManagedSqlite)

Managed C# read-only parser for SQLite3 databases. Provides access to table and row data.

## Install

```
dotnet add package MBW.Utilities.ManagedSqlite
```

To migrate from `MBW.Utilities.ManagedSqlite.Core` or `MBW.Utilities.ManagedSqlite.Sql`, reference `MBW.Utilities.ManagedSqlite` only.

## How to use

```csharp
// Opens MyDb.db, and its -wal or -journal file if one exists
using (Sqlite3Database db = Sqlite3Database.FromPath("MyDb.db"))
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

### Opening a database

Pick the factory that matches the files you have.

> [!IMPORTANT]
> If the database has a `-wal` or `-journal` file next to it, you must pass it in. The `-wal` file can hold committed data that is not in the main file yet, and a hot `-journal` means a write was interrupted and the main file may be half-written. Without them, the library reads stale or inconsistent data, and it cannot warn you: nothing in the main file says whether such a file exists. `FromPath` finds them for you.

| You have | Use |
|---|---|
| A path to the database file | `Sqlite3Database.FromPath(path)` finds `-wal` and `-journal` next to it, as SQLite does |
| The database file only, with no `-wal` or hot `-journal` | `Sqlite3Database.FromStreamWithNoJournal(database)` |
| The database file and its `-wal` file (WAL mode) | `Sqlite3Database.FromStreamWithWal(database, wal)` |
| The database file and its `-journal` file (rollback modes) | `Sqlite3Database.FromStreamWithJournal(database, journal)` |

The stream factories dispose the streams with the database, unless you pass `leaveOpen: true`. Streams must be readable and seekable.

### Journal modes

All of SQLite's journal modes can be read. Nothing is ever written: side files are read in memory only.

* **WAL.** Pages that committed transactions wrote to the `-wal` file are read from there, as SQLite does before a checkpoint copies them into the main file. Uncommitted frames, frames left from before the log restarted, and a damaged or empty log are ignored, as SQLite ignores them. A WAL in a format version other than SQLite's 3007000 throws, as in SQLite. One with a different page size than the database throws in Strict, and is ignored in Lenient. The `-shm` file is never read: it only caches an index that is rebuilt from the `-wal` file.
* **Rollback (DELETE, TRUNCATE, PERSIST).** A hot `-journal`, left by a write that never finished, is played back, so the database reads as it was before that write. This is what a writable SQLite connection does on open. A read-only SQLite connection instead refuses to read the database (`SQLITE_READONLY_ROLLBACK`). A journal that names a super-journal (a transaction across attached databases) is always played back; see [Differences from SQLite](#differences-from-sqlite).
* **MEMORY and OFF** keep no journal on disk; the main file is all there is.

The library assumes nothing else uses the files while a database is open:

* The database and its side files must not be written to while open.
* A lost `-journal` cannot be detected, and the main file may then be half-written. A `-wal` or `-journal` from another database, or an older copy, gives wrong results. SQLite cannot detect these either.

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
* **No locking.** The library takes no locks and assumes exclusive access (see [Journal modes](#journal-modes)). Reading files while another process writes to them can return inconsistent data.

### Differences from SQLite

* **Column DEFAULTs are applied by name only.** A row written before `ALTER TABLE ... ADD COLUMN` does not store the new column, and SQLite reads the column's DEFAULT for it. `TryGetValueByName` does the same; `TryGetOrdinal` and `ColumnData` show the record as stored, without the column. A DEFAULT with a `CAST`, or a negation that is a real in a TEXT column, is not evaluated, and reads as no value. A column without a DEFAULT reads as NULL, as in SQLite.
* **Lenient opens a database whose schema SQLite finds malformed.** As in SQLite, the whole schema is loaded and checked when the database is opened, and in Strict a malformed table, view, index or trigger makes the open throw `SqliteCorruptException`. Lenient reports the error to `OnValidationIssue` and keeps it on the row that failed, so only that row's table cannot be opened, and reads the other tables.
* **Hot journals are played back, not refused.** See [Journal modes](#journal-modes).
* **Super-journals are ignored.** The library reads one database at a time. A hot journal from a transaction across attached databases is always played back, where SQLite plays it back only while the super-journal file still exists. If that transaction did commit, the library reads the database as it was just before it.
* **A WAL with a different page size than the database fails in Strict.** It can only belong to another database. SQLite reads it without an error, leaving part of each page undefined; Lenient ignores the WAL.
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
