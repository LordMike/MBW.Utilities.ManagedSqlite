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
    // List the tables using db.GetTables(), or every schema row (indexes, views and triggers too) using
    // db.GetSchema(), and open one using db.GetTable("name")
    Sqlite3Table tbl = db.GetTable("MyTable");
    
    // Read out the SQL schema of the table (to get column names)
    SqlTableDefinition schema = tbl.GetTableDefinition();

    // Iterate all rows on the table using EnumerateRows()
    // Note that this library does not do SQL selects, it's a raw parser
    IEnumerable<Sqlite3Row> rows = tbl.EnumerateRows();
    
    // Get the data of column 1 (the second column, Name) on the first row, as it is stored in the record
    Sqlite3Row row = rows.First();
    object? data = row.ColumnData[1];

    // TryGetValueByName() reads a column by name, as SQLite does. An INTEGER PRIMARY KEY column is an alias of the
    // row id, which the record stores as NULL, so ColumnData[0] is null for Id; by name, Id reads the row id.
    row.TryGetValueByName("Id", out object? myId);

    // Read column 1 as a C# type: NULL reads as default, and INTEGER and REAL convert into each other as in SQLite
    row.TryGetOrdinal<string>(1, out string? myName);
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

### Settings and validation

Every factory takes an optional `Sqlite3Settings`:

```csharp
Sqlite3Settings settings = new Sqlite3Settings
{
    Validation = SqliteValidation.Lenient,
    OnValidationIssue = issue => Console.WriteLine(issue) // e.g. "HDR-14: ..."
};

using Sqlite3Database db = Sqlite3Database.FromPath("MyDb.db", settings);
```

* **`Validation`** sets how strictly the file is checked. `Strict`, the default, fails exactly where SQLite fails. `Lenient` reads on past damage that SQLite rejects but that leaves the data intact, such as an unknown file format version. Damage that could give wrong values fails in both modes.
* **`OnValidationIssue`** is called for each check that `Lenient` reads past.
* **`FallbackEncoding`** is the text encoding to use when the header's encoding field is 0, which SQLite never writes. The default is UTF-8, as in SQLite. A wrong encoding reads text as garbage.

### Errors

All of the library's own exceptions derive from `SqliteException`:

* **`SqliteCorruptException`**: the file is malformed. `Check` names the consistency check that failed (the IDs are listed in [docs/sqlite-checks](https://github.com/LordMike/MBW.Utilities.ManagedSqlite/blob/master/docs/sqlite-checks/README.md)), and `Page` and `Structure` say where, when known. Damage is reported where SQLite reports it: a malformed header or schema fails the open (in Lenient, a schema row that fails to load fails only the open of its table), a damaged b-tree fails the table's open or its row enumeration, and a bad value fails only the read of that value.
* **`SqliteInvalidEncodingException`**: the settings' `FallbackEncoding` is not a text encoding. This fails the open.

Lookups throw the usual .NET exceptions: `GetTable` throws `KeyNotFoundException` for a name that is no table, and `NotSupportedException` for a virtual table. `GetTableDefinition()` on a schema row of a view, index or trigger throws `InvalidOperationException`, and on a virtual table `NotSupportedException`, since its module declares the columns.

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

* `GetTableDefinition()` also works on a `Sqlite3SchemaRow` from `GetTables()`, so a table's columns can be read without opening the table. It throws as described under [Errors](#errors).
* Threads: an open database can be used from several threads at once. Each stream is read under a lock, so nothing else may read or move the streams while the database is open. An enumeration, and the rows it returns, belong to the thread that runs it. In Lenient, `OnValidationIssue` is called on the reading thread.

## Known limitations

The goal is to read a database file exactly as SQLite itself reads it. These are the known places where it does not, or where it deliberately does less.

### Out of scope

* **Read-only.** There is no way to write, update or delete data.
* **No SQL execution.** Queries, views, triggers and expressions are not run. Rows are read in the order they are stored (rowid order, or primary key order for WITHOUT ROWID tables), and indexes are listed but never used.
* **Virtual tables cannot be read.** Their rows are produced by a SQLite extension (such as fts5 or rtree) at query time, not stored in the file, so `GetTable()` throws `NotSupportedException`. Their shadow tables (for fts5: `<name>_content`, `<name>_data` and so on) are ordinary tables and can be read.
* **VIRTUAL generated columns have no value.** SQLite computes them from their expression each time they are read, and nothing is stored in the file. STORED generated columns are stored and read normally.
* **No locking.** The library takes no locks and assumes exclusive access (see [Journal modes](#journal-modes)). Reading files while another process writes to them can return inconsistent data.

### Differences from SQLite

* **Column DEFAULTs are applied only to a column read through the table definition.** A row written before `ALTER TABLE ... ADD COLUMN` does not store the new column, and SQLite reads the column's DEFAULT for it. `TryGetValueByName` and `TryGetOrdinal(SqlTableColumn)` do the same; `TryGetOrdinal(index)` and `ColumnData` show the record as stored, without the column. A DEFAULT that SQLite evaluates by formatting a real as text, such as `-1.5` in a TEXT column or `CAST(1.5 AS BLOB)`, is not evaluated, and reads as no value. A column without a DEFAULT reads as NULL, as in SQLite.
* **Lenient opens a database whose schema SQLite finds malformed.** As in SQLite, the whole schema is loaded and checked when the database is opened, and in Strict a malformed table, view, index or trigger makes the open throw `SqliteCorruptException`. Lenient reports the error to `OnValidationIssue` and keeps it on the row that failed, so only that row's table cannot be opened, and reads the other tables.
* **Hot journals are played back, not refused.** See [Journal modes](#journal-modes).
* **Super-journals are ignored.** The library reads one database at a time. A hot journal from a transaction across attached databases is always played back, where SQLite plays it back only while the super-journal file still exists. If that transaction did commit, the library reads the database as it was just before it.
* **A WAL with a different page size than the database fails in Strict.** It can only belong to another database. SQLite reads it without an error, leaving part of each page undefined; Lenient ignores the WAL.
* **Rowids out of order fail in Strict.** In a sound table they always increase. SQLite returns the rows as they lie and finds the damage only in `PRAGMA integrity_check`; Strict throws `SqliteCorruptException` (check `INTEG-14`) at the first rowid that does not increase, also in `sqlite_schema` when the database is opened, and Lenient reports it and reads on. In Strict this also stops a corrupt b-tree whose pages share a subtree at its first repeated row; Lenient walks the shared subtree once for each pointer to it, as SQLite does, and reports each repetition.
* **No recovery from corruption.** Reading a damaged table throws, and there is no mode that skips the damaged part and continues.

## Development

* Build with `dotnet build -c Release` and test with `dotnet test -c Release`. The tests use real SQLite (Microsoft.Data.Sqlite) as the oracle: most cases build a database with SQLite and compare what both read.
* Benchmarks against SQLite: see [the benchmarks README](https://github.com/LordMike/MBW.Utilities.ManagedSqlite/blob/master/src/MBW.Utilities.ManagedSqlite.Benchmarks/README.md).
* Conventions for code and tests, for people and agents alike: [AGENTS.md](https://github.com/LordMike/MBW.Utilities.ManagedSqlite/blob/master/AGENTS.md) and [.agents/writing-code.md](https://github.com/LordMike/MBW.Utilities.ManagedSqlite/blob/master/.agents/writing-code.md).
* Every consistency check SQLite makes, and whether this library makes it: [docs/sqlite-checks](https://github.com/LordMike/MBW.Utilities.ManagedSqlite/blob/master/docs/sqlite-checks/README.md).
* The schema parser's tables are generated from SQLite's own `parse.c` by [tools/SqliteGrammarGenerator](https://github.com/LordMike/MBW.Utilities.ManagedSqlite/blob/master/tools/SqliteGrammarGenerator/Program.cs); its header comment says how to run it for a new SQLite version.

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
