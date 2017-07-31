## SqlLite3RO

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

create table Table1 (MyVarchar varchar(10), MyInt smallint);
insert into Table1 values('hello!', 10);
insert into Table1 values('goodbye', 20);
