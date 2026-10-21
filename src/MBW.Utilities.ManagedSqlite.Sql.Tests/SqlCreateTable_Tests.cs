using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Sql.Tests;

/// <summary>
/// The CREATE TABLE syntax SQLite stores in sqlite_schema (https://www.sqlite.org/lang_createtable.html, parse.y).
/// Each theory fixes the shape of the statement, and its rows give only the part that varies: the column list, the
/// table options or the table name, and the rows to insert. Each case creates the table with real SQLite, compares
/// SQLite's view of its schema with the Sql package's parse of the stored statement, and reads its rows by name.
/// </summary>
public class SqlCreateTable_Tests
{
    /// <summary>
    /// <c>CREATE TABLE t (columns); INSERT INTO t VALUES values</c>
    /// <para>
    /// Names come in every quote style, with the quote character escaped by doubling it, or unquoted. SQLite lets any
    /// character outside ASCII, '_' and '$' be part of an unquoted identifier, and only space, tab, line feed, form
    /// feed and carriage return are whitespace (a vertical tab only inside a run of it). A byte order mark where a
    /// token starts is whitespace. Names ignore the case of ASCII letters only, so é and É are two columns.
    /// </para>
    /// <para>
    /// The declared type is the words between the column name and its first constraint, unquoted, without a size.
    /// Table constraints follow the columns, separated by commas or by nothing at all (tconscomma ::= in parse.y).
    /// </para>
    /// <para>
    /// A comma separates column definitions and table constraints only at the top level of the column list, never
    /// inside parentheses, literals or comments. Every operator and literal form the tokenizer knows appears in a
    /// CHECK.
    /// </para>
    /// </summary>
    public static TheoryData<string, string> Columns() => new()
    {
        // Names
        { "\"\"\"\" TEXT, 'col''two' TEXT, `col``two` TEXT, [col two] TEXT, \"a'b`c\" TEXT, \"my\"\"id\" INTEGER, PRIMARY KEY (\"my\"\"id\")", "(1, 2, 3, 4, 5, 6)" },
        { "\"a,b\" INTEGER, [x,y] TEXT, `p,q` REAL, 'r,s' BLOB, \"c\"\",\" TEXT, PRIMARY KEY (\"a,b\", [x,y])", "(1, 2, 3, 4, 5)" },
        { "\"primary\" INTEGER, \"check\" TEXT, \"constraint\" TEXT, [unique], `foreign`, \"select\", \"a b\"", "(1, 2, 3, 4, 5, 6, 7)" },
        // Keywords SQLite lets stand for a name (%fallback ID in parse.y)
        { "key TEXT, desc INTEGER, temp, replace TEXT, action, if, view, row, generated, always", "(1, 2, 3, 4, 5, 6, 7, 8, 9, 10)" },
        { "a€ INTEGER, b² TEXT, c😀 REAL, 日本 BLOB, _a$1 INTEGER", "(1, 2, 3, 4, 5)" },
        { "é TEXT, É TEXT", "('lower', 'upper')" },
        // A vertical tab that follows no other whitespace is part of the name, so the second column is \vb, typed TEXT
        { "a INTEGER, \u000Bb\u000CTEXT", "(1, 2)" },
        { "﻿a INTEGER, b", "(1, 2)" },
        // Types
        { "a integer, b BIGINT PRIMARY KEY, c VARCHAR(10), d NVARCHAR(5) NOT NULL, e, f BLOB CONSTRAINT nn NOT NULL, g STRING", "(1, 2, 3, 4, 5, 6, 7)" },
        { "a VARYING CHARACTER(255), b NATIVE CHARACTER(70), c UNSIGNED BIG INT, d LONG DOUBLE, e double precision, f DOUBLE TEXT", "(1, 2, 3, 4, 5, 6)" },
        { "a DECIMAL(10, 5), b VARCHAR( 10 , 2 ), c DECIMAL(+10, -5), d TEXT", "(1, 2, 3, 4)" },
        { "a 'INTEGER', b [TEXT], c \"REAL\", d `BLOB`", "(1, 2, 3, 4)" },
        { "a PRIMARY KEY, b NOT NULL DEFAULT 1, c UNIQUE, d CHECK (d > 0), e DEFAULT NULL", "(1, 2, 3, 4, 5)" },
        // GENERATED and ALWAYS can be type words. SQLite drops a trailing "always" or "generated always" from a type
        // of 16 or more characters, so d keeps its ALWAYS.
        { "a INT GENERATED ALWAYS, b BIGINTEGER ALWAYS, c INT GENERATED, d SHORT ALWAYS, e", "(1, 2, 3, 4, 5)" },
        // Column constraints
        { "a INTEGER DEFAULT -1, b REAL DEFAULT +1.5, c TEXT DEFAULT CURRENT_TIMESTAMP, d BLOB DEFAULT x'00ff', e DEFAULT NULL, f DEFAULT TRUE, g TEXT DEFAULT abc, h INTEGER DEFAULT 0x10, i TEXT DEFAULT 'x, y'", "(1, 2, 3, 4, 5, 6, 7, 8, 9)" },
        { "a TEXT DEFAULT (printf('%d,%d', 1, 2)), b TEXT DEFAULT (replace('x,y', ',', ';')), c INTEGER", "(1, 2, 3)" },
        { "a TEXT NULL NOT NULL, b TEXT UNIQUE ON CONFLICT REPLACE, c TEXT COLLATE NOCASE, d TEXT CONSTRAINT c1 CHECK (d <> ''), e TEXT CONSTRAINT lonely, f INTEGER NOT NULL ON CONFLICT FAIL", "('a', 'b', 'c', 'd', 'e', 1)" },
        { "a TEXT CHECK (instr(a, ',') = 0 AND length(substr(a, 1, 2)) > 0), b INTEGER, CHECK (coalesce(a, b) IS NOT NULL)", "('a', 1)" },
        { "a INTEGER REFERENCES p(x) ON DELETE SET NULL ON UPDATE CASCADE MATCH SIMPLE DEFERRABLE INITIALLY DEFERRED, b INTEGER REFERENCES p NOT DEFERRABLE INITIALLY IMMEDIATE, c INTEGER REFERENCES p ON DELETE NO ACTION ON UPDATE RESTRICT ON DELETE SET DEFAULT", "(1, 2, 3)" },
        // Table constraints
        { "id INTEGER PRIMARY KEY, a INTEGER, b TEXT, UNIQUE (a, b)", "(1, 2, 'x')" },
        { "a INTEGER, b TEXT, c REAL, CONSTRAINT pk_t PRIMARY KEY (a, b)", "(1, 'b', 1.5)" },
        { "a, b, FOREIGN KEY (a, b) REFERENCES p (x, y) ON UPDATE SET NULL MATCH FULL DEFERRABLE INITIALLY IMMEDIATE, CONSTRAINT u UNIQUE (a) ON CONFLICT ROLLBACK, CONSTRAINT ck CHECK (a IS NOT b)", "(1, 2)" },
        { "a, b, c, UNIQUE (c) CHECK (c > 0) PRIMARY KEY (b)", "(1, 2, 3)" },
        // SQLite takes a string literal in the key as a name, for compatibility
        { "a INTEGER, b, PRIMARY KEY ('a')", "(42, 'x')" },
        // Tokens
        { "a TEXT DEFAULT 'x, y', b TEXT DEFAULT ',' CHECK (b <> ', ('), c INTEGER", "('a', 'b', 1)" },
        { "a INTEGER, -- a, b\n b TEXT /* , */, c /* ,, */ REAL -- ,\n", "(1, 2, 3)" },
        { "\r\n  a INTEGER, -- don't (count, this)\r\n  /* b TEXT, ( ' \" */ c TEXT -- ) closing paren is in a comment\r\n", "(1, 'x')" },
        { "\n  a INTEGER\n  ,b TEXT ,c REAL\n,\n\td BLOB , e ,f\n", "(1, 2, 3, 4, 5, 6)" },
        { "a INTEGER CHECK (a = 1 OR a == 1 OR a != 2 OR a <> 2 OR a < 9 OR a <= 9 OR a > 0 OR a >= 0), b TEXT, CHECK (a <> 3)", "(1, 'x')" },
        { "a INTEGER, b TEXT, CHECK (a * 2 / 3 % 4 - 5 + 6 > 0 OR ((a & 1) | (a << 2) | (a >> 3) | ~a) > 0 OR b || 'x' = 'yx' OR b -> '$.x' IS NOT NULL OR b ->> '$.y' IS NULL)", "(1, 'x')" },
        { "a REAL CHECK (a > -1.5e-3 AND a < .5 AND a <> 0x1F AND a <> 1E6 AND a <> 2.), b INTEGER DEFAULT 1_000", "(0.25, 1)" },
        { "a BLOB CHECK (a <> X'00' AND a <> x''), b", "(x'01', 1)" },
        { "a, b, CHECK (CASE WHEN a IS NULL THEN 1 ELSE a BETWEEN 1 AND 9 END), CHECK (a IN (1, 2, 3) OR a LIKE 'x%' ESCAPE '\\' OR a GLOB '*' OR b NOT NULL OR a IS NOT DISTINCT FROM b)", "(1, 2)" },
    };

    /// <summary>
    /// <c>CREATE TABLE name (a INTEGER, b TEXT); INSERT INTO name VALUES (1, 'x')</c>
    /// </summary>
    public static TheoryData<string> TableNames() => new()
    {
        "\"my\"\"table\"",
        "\"t,1\"",
        "\"my table\"",
        "\"order\"",
        "[a b]",
        "`c``d`",
    };

    /// <summary>
    /// <c>CREATE TABLE t (columns); INSERT INTO t (id, v) VALUES (42, 'x'), (-7, 'y')</c>
    /// <para>
    /// SQLite makes a column an alias for the rowid only when its declared type is exactly "INTEGER" (any case, once
    /// unquoted), it is the sole PRIMARY KEY column, and the table is a rowid table
    /// (https://www.sqlite.org/lang_createtable.html#rowid). An alias reads 42 and -7 as the rowids, a non-alias reads
    /// them as stored values next to rowids 1 and 2.
    /// </para>
    /// </summary>
    public static TheoryData<string> RowIdAliases() => new()
    {
        // An alias
        "id INTEGER PRIMARY KEY, v TEXT",
        "id integer primary key, v text",
        "id InTeGeR PRIMARY KEY AUTOINCREMENT, v TEXT",
        "id INTEGER PRIMARY KEY ASC, v TEXT",
        "id INTEGER NOT NULL PRIMARY KEY, v TEXT",
        "id INTEGER CONSTRAINT pk PRIMARY KEY, v TEXT",
        "id INTEGER, v TEXT, PRIMARY KEY (id)",
        "id integer, v TEXT, primary key (id)",
        "id INTEGER, v TEXT, PRIMARY KEY (id DESC)",
        "id INTEGER, v TEXT, CONSTRAINT pk PRIMARY KEY (id)",
        // The type is compared once unquoted
        "id 'INTEGER' PRIMARY KEY, v TEXT",
        "id [INTEGER] PRIMARY KEY, v TEXT",
        // SQLite drops "GENERATED ALWAYS" from the end of a long enough type
        "id INTEGER GENERATED ALWAYS PRIMARY KEY, v TEXT",
        // DEFERRABLE is a column constraint, so the type is still just INTEGER
        "id INTEGER DEFERRABLE PRIMARY KEY, v TEXT",
        "id INTEGER PRIMARY KEY ASC ON CONFLICT ABORT AUTOINCREMENT, v TEXT",
        // Table constraints need no comma between them
        "id INTEGER, v TEXT, UNIQUE (v) PRIMARY KEY (id)",
        "id INTEGER, v TEXT, CHECK (id <> 0) CONSTRAINT pk PRIMARY KEY (id)",
        // Not an alias
        "id INT PRIMARY KEY, v TEXT",
        "id BIGINT PRIMARY KEY, v TEXT",
        "id UNSIGNED INTEGER PRIMARY KEY, v TEXT",
        "id INTEGER UNSIGNED PRIMARY KEY, v TEXT",
        "id INTEGER(10) PRIMARY KEY, v TEXT",
        "id INTEGER PRIMARY KEY DESC, v TEXT",
        "id INT, v TEXT, PRIMARY KEY (id)",
        "id INTEGER, v TEXT, PRIMARY KEY (id, v)",
    };

    /// <summary>
    /// <c>CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT) options; INSERT INTO t VALUES (42, 'x'), (-7, 'y')</c>
    /// <para>
    /// Options follow the column list, separated by commas. An INTEGER PRIMARY KEY aliases the rowid only in a rowid
    /// table, so the alias shows whether WITHOUT ROWID was seen.
    /// </para>
    /// </summary>
    public static TheoryData<string> TableOptions() => new()
    {
        "WITHOUT ROWID",
        "STRICT",
        "STRICT, WITHOUT ROWID",
        "WITHOUT ROWID, STRICT",
        "strict, without rowid",
    };

    /// <summary>
    /// <c>CREATE TABLE t (columns) WITHOUT ROWID; INSERT INTO t VALUES values</c>
    /// <para>
    /// The table is an index b-tree. Its record starts with the primary key terms in key order, then the other stored
    /// columns in declared order. A term that repeats an earlier one with the same collation is left out (isDupColumn
    /// in https://sqlite.org/src/file?name=src/build.c).
    /// </para>
    /// </summary>
    public static TheoryData<string, string> WithoutRowIdColumns() => new()
    {
        { "a TEXT, b INTEGER, c, PRIMARY KEY (a COLLATE NOCASE DESC, b ASC) ON CONFLICT IGNORE", "('x', 1, 'c'), ('y', 2, 'd')" },
        // The record holds b, then a and c
        { "a, b, c, UNIQUE (c) CHECK (c > 0) PRIMARY KEY (b)", "(1, 2, 3)" },
        // The record holds a, b, then v. The repeated a is dropped.
        { "v TEXT, b INTEGER, a TEXT, PRIMARY KEY (a, b, a)", "('first', 2, 'y'), ('second', 1, 'y'), ('third', 9, 'x')" },
        // a is in the key twice, as two terms that collate differently, so the record holds it twice
        { "a TEXT, b TEXT, c TEXT COLLATE NOCASE, PRIMARY KEY (a, a COLLATE NOCASE, c, c COLLATE nocase)", "('A', 'b', 'C')" },
        // The last of a column's COLLATE constraints is its collation, so g's two key terms differ
        { "g TEXT COLLATE NOCASE COLLATE RTRIM, h TEXT, PRIMARY KEY (g, g COLLATE NOCASE, h)", "('G', 'h')" },
        // The record holds k, v, w. The VIRTUAL column g takes no position.
        { "v TEXT, g AS (v || '!') VIRTUAL, w INTEGER, k TEXT PRIMARY KEY", "('first', 1, 'b'), ('second', 2, 'a')" },
    };

    /// <summary>
    /// <c>CREATE TABLE t (columns) STRICT; INSERT INTO t VALUES values</c>
    /// </summary>
    public static TheoryData<string, string> StrictColumns() => new()
    {
        { "a ANY, b INT, c REAL, d TEXT, e BLOB, f INTEGER", "(1, 2, 3, 'x', x'00', 4)" },
    };

    /// <summary>
    /// <c>CREATE TABLE t (columns); INSERT INTO t VALUES values</c>, the values leaving out the generated columns
    /// <para>
    /// A STORED generated column is in the record at its position. A VIRTUAL one (the default) is not stored, so the
    /// columns after it sit one position left there, and it has no value.
    /// </para>
    /// </summary>
    public static TheoryData<string, string> GeneratedColumns() => new()
    {
        // The AS inside f's CHECK does not make it a generated column
        { "a INTEGER, b INTEGER, c INTEGER AS (coalesce(a, b)), d INTEGER GENERATED ALWAYS AS (max(a, b)) STORED, e TEXT, f INTEGER CHECK (CAST(f AS TEXT) <> ''), g TEXT COLLATE NOCASE GENERATED ALWAYS AS (upper(e)) STORED", "(1, 2, 'x', 3)" },
        { "a INTEGER, v AS (a * 2) VIRTUAL, s INTEGER AS (a * 3) STORED, z TEXT", "(1, 'one'), (2, 'two')" },
    };

    /// <summary>
    /// <c>CREATE VIRTUAL TABLE t USING module; INSERT INTO t VALUES values</c>
    /// <para>
    /// A virtual table is only listed. Its shadow tables are ordinary tables and are read, fts5's t_idx and t_config
    /// being WITHOUT ROWID.
    /// </para>
    /// </summary>
    public static TheoryData<string, string> VirtualTables() => new()
    {
        { "fts5(title, body)", "('hello world', 'first'), ('second row', 'hello again')" },
    };

    [Theory]
    [MemberData(nameof(Columns))]
    public void Column(string columns, string values) => AssertSameAsSqlite($"CREATE TABLE t ({columns}); INSERT INTO t VALUES {values};");

    [Theory]
    [MemberData(nameof(TableNames))]
    public void TableName(string name) => AssertSameAsSqlite($"CREATE TABLE {name} (a INTEGER, b TEXT); INSERT INTO {name} VALUES (1, 'x');");

    [Theory]
    [MemberData(nameof(RowIdAliases))]
    public void RowIdAlias(string columns) => AssertSameAsSqlite($"CREATE TABLE t ({columns}); INSERT INTO t (id, v) VALUES (42, 'x'), (-7, 'y');");

    [Theory]
    [MemberData(nameof(TableOptions))]
    public void TableOption(string options) => AssertSameAsSqlite($"CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT) {options}; INSERT INTO t VALUES (42, 'x'), (-7, 'y');");

    [Theory]
    [MemberData(nameof(WithoutRowIdColumns))]
    public void WithoutRowIdColumn(string columns, string values) => AssertSameAsSqlite($"CREATE TABLE t ({columns}) WITHOUT ROWID; INSERT INTO t VALUES {values};");

    [Theory]
    [MemberData(nameof(StrictColumns))]
    public void StrictColumn(string columns, string values) => AssertSameAsSqlite($"CREATE TABLE t ({columns}) STRICT; INSERT INTO t VALUES {values};");

    [Theory]
    [MemberData(nameof(GeneratedColumns))]
    public void GeneratedColumn(string columns, string values) => AssertSameAsSqlite($"CREATE TABLE t ({columns}); INSERT INTO t VALUES {values};");

    [Theory]
    [MemberData(nameof(VirtualTables))]
    public void VirtualTable(string module, string values) => AssertSameAsSqlite($"CREATE VIRTUAL TABLE t USING {module}; INSERT INTO t VALUES {values};");

    private static void AssertSameAsSqlite(string script)
    {
        // REFERENCES may name a table that does not exist
        script = "PRAGMA foreign_keys = OFF; " + script;
        SqliteParity.AssertSchemaSameAsSqlite(script);
        SqliteParity.AssertSameAsSqlite(script);
    }
}
