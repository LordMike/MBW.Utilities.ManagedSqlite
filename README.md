## MBW.Utilities.ManagedSqlite [![Generic Build](https://github.com/LordMike/MBW.Utilities.ManagedSqlite/actions/workflows/dotnet.yml/badge.svg)](https://github.com/LordMike/MBW.Utilities.ManagedSqlite/actions/workflows/dotnet.yml) [![NuGet](https://img.shields.io/nuget/v/MBW.Utilities.ManagedSqlite.Core.svg)](https://www.nuget.org/packages/MBW.Utilities.ManagedSqlite.Core) [![GHPackages](https://img.shields.io/badge/package-alpha-green)](https://github.com/LordMike/MBW.Utilities.ManagedSqlite/packages/692005)

Managed C# read-only parser for SQLite3 databases. Provides access to table and row data.

## Packages

| Package | Nuget | Alpha |
| ------------- |:-------------:|:-------------:|
| MBW.Utilities.ManagedSqlite.Core | [![NuGet](https://img.shields.io/nuget/v/MBW.Utilities.ManagedSqlite.Core.svg)](https://www.nuget.org/packages/MBW.Utilities.ManagedSqlite.Core) | [Alpha](https://github.com/LordMike/MBW.Utilities.ManagedSqlite/packages/692005) |
| MBW.Utilities.ManagedSqlite.Sql | [![NuGet](https://img.shields.io/nuget/v/MBW.Utilities.ManagedSqlite.Sql.svg)](https://www.nuget.org/packages/MBW.Utilities.ManagedSqlite.Sql) | [Alpha](https://github.com/LordMike/MBW.Utilities.ManagedSqlite/packages/692006) |

## How to use

```csharp
using (Stream fs = File.OpenRead("MyDb.db"))
using (Sqlite3Database db = new Sqlite3Database(fs))
{
    // Access all tables using db.GetTables(), or a single using db.GetTable("name")
    Sqlite3Table tbl = db.GetTable("MyTable");
    
    // Read out the SQL schema of the table (to get column names) using the MBW.Utilities.ManagedSqlite.Sql package
    SqlTableDefinition schema = tbl.GetTableDefinition();

    // Iterate all rows on the table using EnumerateRows()
    // Note that this library does not do SQL selects, it's a raw parser
    IEnumerable<Sqlite3Row> rows = tbl.EnumerateRows();
    
    // Get the data of column 0 on row 0
    Sqlite3Row row = rows.First();
    object data = row.ColumnData[0];

    // Using the SQL package also provides a TryGetValueByName(), this package also tries to detect row-id substitutes
    row.TryGetValueByName("Id", out var myId); // This actually reads the row id behind the scenes, if Id is an Integer Primary Key
    
    // Try casting it to a specific type (you must know the C# type)
    row.TryGetOrdinal<int>(0, out int myInteger);
}
```

### Features

* Reads SQLite3 databases, in all managed code
* Provides close-to-raw access to the structured data
* An SQL package exists to help parse the SQLite3 schemas

## Notes

* This is not an ADO.Net substitute, it will not support SQL statements or use indexes - it will only provide access to the data in a database.
* All the SQL parsing logic is basic, it will work in most cases. You're welcome to present cases where it does not work.
* The library has limited support for handling cases where the primary key of a table is the [row-id](https://www.sqlite.org/lang_createtable.html#rowid). 
* Interestingly, SQLite3 allows _any_ value in _any_ column. So you may get different types out of the raw files, should you encounter an app that does this.

## SQLite3 Docs
* Format changes: https://www.sqlite.org/formatchng.html
* File format: https://www.sqlite.org/fileformat.html

## Data types mapping
Source: https://www.techonthenet.com/sqlite/datatypes.php

Note: Sqlite only has 5 data types: https://sqlite.org/datatype3.html

The following translate to `string`.
```
CHAR(size)			-- Equivalent to TEXT (size is ignored)
VARCHAR(size)		-- Equivalent to TEXT (size is ignored)
TINYTEXT(size)		-- Equivalent to TEXT (size is ignored)
TEXT(size)			-- Equivalent to TEXT (size is ignored)
MEDIUMTEXT(size)	-- Equivalent to TEXT (size is ignored)
LONGTEXT(size)		-- Equivalent to TEXT (size is ignored)
NCHAR(size)			-- Equivalent to TEXT (size is ignored)
NVARCHAR(size)		-- Equivalent to TEXT (size is ignored)
CLOB(size)			-- Equivalent to TEXT (size is ignored)
```

The following translate to `long`.
```
TINYINT				-- Equivalent to INTEGER
SMALLINT			-- Equivalent to INTEGER
MEDIUMINT			-- Equivalent to INTEGER
INT					-- Equivalent to INTEGER
INTEGER				-- Equivalent to INTEGER
BIGINT				-- Equivalent to INTEGER
INT2				-- Equivalent to INTEGER
INT4				-- Equivalent to INTEGER
INT8				-- Equivalent to INTEGER
NUMERIC				-- Equivalent to NUMERIC
DECIMAL				-- Equivalent to NUMERIC
BOOLEAN				-- Equivalent to NUMERIC
DATE				-- Equivalent to NUMERIC
DATETIME			-- Equivalent to NUMERIC
TIMESTAMP			-- Equivalent to NUMERIC
TIME				-- Equivalent to NUMERIC
```

The following translate to `double`.
```
REAL				-- Equivalent to REAL
DOUBLE				-- Equivalent to REAL
DOUBLE PRECISION	-- Equivalent to REAL
FLOAT				-- Equivalent to REAL
```

The following translate to `byte[]`.
```
BLOB				-- Equivalent to NONE
```
