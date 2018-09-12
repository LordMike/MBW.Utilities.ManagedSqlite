## MBW.Utilities.ManagedSqlite

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
    object data = rows.ColumnData[0];

    // Try casting it to a specific type (you must know the C# type)
    row.TryGetOrdinal<int>(0, out int myInteger);
}
```

## Packages

| Package | Nuget |
| ------------- |:-------------:|
| MBW.Utilities.ManagedSqlite.Core | [![NuGet](https://img.shields.io/nuget/v/MBW.Utilities.ManagedSqlite.Core.svg)](https://www.nuget.org/packages/MBW.Utilities.ManagedSqlite.Core) |
| MBW.Utilities.ManagedSqlite.Sql | [![NuGet](https://img.shields.io/nuget/v/MBW.Utilities.ManagedSqlite.Sql.svg)](https://www.nuget.org/packages/MBW.Utilities.ManagedSqlite.Sql) |

### Features

* Reads SQLite3 databases, in all managed code
* Provides close-to-raw access to the structured data
* An SQL package exists to help parse the SQLite3 schemas

## Docs
* Format changes: https://www.sqlite.org/formatchng.html
* File format: https://www.sqlite.org/fileformat.html

## Data types
Source: https://www.techonthenet.com/sqlite/datatypes.php

Note: Sqlite only has 5 data types: https://sqlite.org/datatype3.html

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
REAL				-- Equivalent to REAL
DOUBLE				-- Equivalent to REAL
DOUBLE PRECISION	-- Equivalent to REAL
FLOAT				-- Equivalent to REAL
```

```
BOOLEAN				-- Equivalent to NUMERIC
DATE				-- Equivalent to NUMERIC
DATETIME			-- Equivalent to NUMERIC
TIMESTAMP			-- Equivalent to NUMERIC
TIME				-- Equivalent to NUMERIC
```

```
BLOB				-- Equivalent to NONE
```
