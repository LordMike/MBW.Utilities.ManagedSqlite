using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Sql.Tests;

/// <summary>
/// The CREATE TABLE syntax SQLite stores in sqlite_schema, by area of its grammar
/// (https://www.sqlite.org/lang_createtable.html, parse.y). Each case creates tables with real SQLite, compares
/// SQLite's view of their schema with the Sql package's parse of the stored statements, and reads their rows by name.
/// </summary>
public class SqlSchemaParity_Tests
{
    /// <summary>
    /// Names in every quote style, with the quote character escaped by doubling it, and unquoted names. SQLite lets any
    /// character outside ASCII, '_' and '$' be part of an unquoted identifier, and only space, tab, line feed, form feed
    /// and carriage return are whitespace (a vertical tab only inside a run of it), so a no-break space joins the
    /// identifier it touches: a INTEGER is one untyped column. A byte order mark where a token starts is
    /// whitespace. Names ignore the case of ASCII letters only, so é and É are two columns.
    /// </summary>
    public static TheoryData<string, string> Names() => new()
    {
        { "quoted names", "CREATE TABLE \"my\"\"table\" (\"\"\"\" TEXT, 'col''two' TEXT, `col``two` TEXT, [col two] TEXT, \"a'b`c\" TEXT, \"my\"\"id\" INTEGER, PRIMARY KEY (\"my\"\"id\"))" },
        { "quoted names holding commas", "CREATE TABLE \"t,1\" (\"a,b\" INTEGER, [x,y] TEXT, `p,q` REAL, 'r,s' BLOB, \"c\"\",\" TEXT, PRIMARY KEY (\"a,b\", [x,y]))" },
        { "constraint keywords as quoted names", "CREATE TABLE t (\"primary\" INTEGER, \"check\" TEXT, \"constraint\" TEXT, [unique], `foreign`); INSERT INTO t VALUES (1, 2, 3, 4, 5);" },
        // Keywords SQLite lets stand for a name (%fallback ID in parse.y)
        { "keywords as unquoted names", "CREATE TABLE t (key TEXT, desc INTEGER, temp, replace TEXT, action, if, view, row, generated, always); INSERT INTO t VALUES (1, 2, 3, 4, 5, 6, 7, 8, 9, 10);" },
        { "unquoted names outside ASCII", "CREATE TABLE t (a€ INTEGER, b² TEXT, c😀 REAL, 日本 BLOB, _a$1 INTEGER)" },
        { "names differing in non-ASCII case", "CREATE TABLE t (é TEXT, É TEXT); INSERT INTO t VALUES ('lower', 'upper');" },
        { "whitespace SQLite does not know", "CREATE TABLE t (a INTEGER, \u000Bb\u000CTEXT)" },
        { "byte order mark", "CREATE TABLE t (﻿a INTEGER, b); INSERT INTO t VALUES (1, 2);" },
    };

    /// <summary>
    /// The declared type is the words between the column name and its first constraint, unquoted, without a size
    /// </summary>
    public static TheoryData<string, string> Types() => new()
    {
        { "declared types", "CREATE TABLE t (a integer, b BIGINT PRIMARY KEY, c VARCHAR(10), d NVARCHAR(5) NOT NULL, e, f BLOB CONSTRAINT nn NOT NULL, g STRING)" },
        { "multi-word types", "CREATE TABLE t (a VARYING CHARACTER(255), b NATIVE CHARACTER(70), c UNSIGNED BIG INT, d LONG DOUBLE, e double precision, f DOUBLE TEXT); INSERT INTO t VALUES (1, 2, 3, 4, 5, 6);" },
        { "sizes", "CREATE TABLE t (a DECIMAL(10, 5), b VARCHAR( 10 , 2 ), c DECIMAL(+10, -5), d TEXT)" },
        { "quoted types", "CREATE TABLE t (a 'INTEGER', b [TEXT], c \"REAL\", d `BLOB`); INSERT INTO t VALUES (1, 2, 3, 4);" },
        { "untyped columns with constraints", "CREATE TABLE t (a PRIMARY KEY, b NOT NULL DEFAULT 1, c UNIQUE, d CHECK (d > 0), e DEFAULT NULL); INSERT INTO t VALUES (1, 2, 3, 4, 5);" },
        // GENERATED and ALWAYS can be type words. SQLite drops a trailing "always" or "generated always" from a type
        // of 16 or more characters, so d keeps its ALWAYS.
        { "GENERATED and ALWAYS in types", "CREATE TABLE t (a INT GENERATED ALWAYS, b BIGINTEGER ALWAYS, c INT GENERATED, d SHORT ALWAYS, e); INSERT INTO t VALUES (1, 2, 3, 4, 5);" },
    };

    public static TheoryData<string, string> ColumnConstraints() => new()
    {
        { "DEFAULT forms", "CREATE TABLE t (a INTEGER DEFAULT -1, b REAL DEFAULT +1.5, c TEXT DEFAULT CURRENT_TIMESTAMP, d BLOB DEFAULT x'00ff', e DEFAULT NULL, f DEFAULT TRUE, g TEXT DEFAULT abc, h INTEGER DEFAULT 0x10, i TEXT DEFAULT 'x, y'); INSERT INTO t DEFAULT VALUES;" },
        { "DEFAULT expressions", "CREATE TABLE t (a TEXT DEFAULT (printf('%d,%d', 1, 2)), b TEXT DEFAULT (replace('x,y', ',', ';')), c INTEGER)" },
        { "NULL, NOT NULL, UNIQUE, COLLATE and named constraints", "CREATE TABLE t (a TEXT NULL NOT NULL, b TEXT UNIQUE ON CONFLICT REPLACE, c TEXT COLLATE NOCASE, d TEXT CONSTRAINT c1 CHECK (d <> ''), e TEXT CONSTRAINT lonely, f INTEGER NOT NULL ON CONFLICT FAIL); INSERT INTO t VALUES ('a', 'b', 'c', 'd', 'e', 1);" },
        { "CHECK with function calls, also as a table constraint", "CREATE TABLE t (a TEXT CHECK (instr(a, ',') = 0 AND length(substr(a, 1, 2)) > 0), b INTEGER, CHECK (coalesce(a, b) IS NOT NULL))" },
        { "REFERENCES with actions and DEFERRABLE", "PRAGMA foreign_keys = OFF; CREATE TABLE p (x INTEGER PRIMARY KEY); CREATE TABLE t (a INTEGER REFERENCES p(x) ON DELETE SET NULL ON UPDATE CASCADE MATCH SIMPLE DEFERRABLE INITIALLY DEFERRED, b INTEGER REFERENCES p NOT DEFERRABLE INITIALLY IMMEDIATE, c INTEGER REFERENCES p ON DELETE NO ACTION ON UPDATE RESTRICT ON DELETE SET DEFAULT); INSERT INTO t VALUES (1, 2, 3);" },
        // c is VIRTUAL (the default), d STORED, and the AS inside f's CHECK does not make it a generated column
        { "generated columns", "CREATE TABLE t (a INTEGER, b INTEGER, c INTEGER AS (coalesce(a, b)), d INTEGER GENERATED ALWAYS AS (max(a, b)) STORED, e TEXT, f INTEGER CHECK (CAST(f AS TEXT) <> ''), g TEXT COLLATE NOCASE GENERATED ALWAYS AS (upper(e)) STORED); INSERT INTO t (a, b, e, f) VALUES (1, 2, 'x', 3);" },
    };

    /// <summary>
    /// Table constraints follow the columns, separated by commas or by nothing at all (tconscomma ::= in parse.y)
    /// </summary>
    public static TheoryData<string, string> TableConstraints() => new()
    {
        { "UNIQUE (a, b)", "CREATE TABLE t (id INTEGER PRIMARY KEY, a INTEGER, b TEXT, UNIQUE (a, b))" },
        { "CONSTRAINT name PRIMARY KEY (a, b)", "CREATE TABLE t (a INTEGER, b TEXT, c REAL, CONSTRAINT pk_t PRIMARY KEY (a, b))" },
        { "FOREIGN KEY with actions, named UNIQUE and CHECK", "PRAGMA foreign_keys = OFF; CREATE TABLE p (x, y, PRIMARY KEY (x, y)); CREATE TABLE t (a, b, FOREIGN KEY (a, b) REFERENCES p (x, y) ON UPDATE SET NULL MATCH FULL DEFERRABLE INITIALLY IMMEDIATE, CONSTRAINT u UNIQUE (a) ON CONFLICT ROLLBACK, CONSTRAINT ck CHECK (a IS NOT b)); INSERT INTO t VALUES (1, 2);" },
        { "PRIMARY KEY with COLLATE, sort orders and ON CONFLICT", "CREATE TABLE t (a TEXT, b INTEGER, c, PRIMARY KEY (a COLLATE NOCASE DESC, b ASC) ON CONFLICT IGNORE) WITHOUT ROWID; INSERT INTO t VALUES ('x', 1, 'c');" },
        // SQLite takes a string literal in the key as a name, for compatibility
        { "PRIMARY KEY of a string literal", "CREATE TABLE t (a INTEGER, b, PRIMARY KEY ('a')); INSERT INTO t VALUES (42, 'x');" },
        // The record of a WITHOUT ROWID table starts with the key terms, here b, then a and c
        { "constraints without commas", "CREATE TABLE t (a, b, c, UNIQUE (c) CHECK (c > 0) PRIMARY KEY (b)) WITHOUT ROWID; INSERT INTO t VALUES (1, 2, 3);" },
        // a is in the key twice, as two terms that collate differently, so the record holds it twice
        { "PRIMARY KEY repeating a column with another collation", "CREATE TABLE t (a TEXT, b TEXT, c TEXT COLLATE NOCASE, PRIMARY KEY (a, a COLLATE NOCASE, c, c COLLATE nocase)) WITHOUT ROWID; INSERT INTO t VALUES ('A', 'b', 'C');" },
        // The last of a column's COLLATE constraints is its collation, so g's two key terms differ
        { "column with two COLLATE constraints in the key twice", "CREATE TABLE t (g TEXT COLLATE NOCASE COLLATE RTRIM, h TEXT, PRIMARY KEY (g, g COLLATE NOCASE, h)) WITHOUT ROWID; INSERT INTO t VALUES ('G', 'h');" },
    };

    /// <summary>
    /// Options after the column list, separated by commas. An INTEGER PRIMARY KEY aliases the rowid only in a rowid
    /// table, so the alias shows whether WITHOUT ROWID was seen.
    /// </summary>
    public static TheoryData<string, string> TableOptions() => new()
    {
        { "STRICT, WITHOUT ROWID", "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT) STRICT, WITHOUT ROWID" },
        { "WITHOUT ROWID, STRICT", "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT) WITHOUT ROWID, STRICT" },
        { "lower case options", "CREATE TABLE t (id integer primary key, v text) strict, without rowid; INSERT INTO t VALUES (1, 'x');" },
        { "STRICT with every type", "CREATE TABLE t (a ANY, b INT, c REAL, d TEXT, e BLOB, f INTEGER) STRICT; INSERT INTO t VALUES (1, 2, 3, 'x', x'00', 4);" },
    };

    /// <summary>
    /// A comma separates column definitions and table constraints only at the top level of the column list, never
    /// inside parentheses, literals or comments. Every operator and literal form the tokenizer knows appears in a
    /// CHECK; a token it does not know makes the table unparseable.
    /// </summary>
    public static TheoryData<string, string> Tokens() => new()
    {
        { "string literals", "CREATE TABLE t (a TEXT DEFAULT 'x, y', b TEXT DEFAULT ',' CHECK (b <> ', ('), c INTEGER)" },
        { "comments", "CREATE TABLE t (a INTEGER, -- a, b\n b TEXT /* , */, c /* ,, */ REAL -- ,\n)" },
        { "comments holding quotes and parentheses", "CREATE TABLE t (\r\n  a INTEGER, -- don't (count, this)\r\n  /* b TEXT, ( ' \" */ c TEXT -- ) closing paren is in a comment\r\n)" },
        { "newlines and odd spacing", "CREATE TABLE t (\n  a INTEGER\n  ,b TEXT ,c REAL\n,\n\td BLOB , e ,f\n)" },
        { "comparison operators", "CREATE TABLE t (a INTEGER CHECK (a = 1 OR a == 1 OR a != 2 OR a <> 2 OR a < 9 OR a <= 9 OR a > 0 OR a >= 0), b TEXT, CHECK (a <> 3))" },
        { "arithmetic, bitwise, concatenation and JSON operators", "CREATE TABLE t (a INTEGER, b TEXT, CHECK (a * 2 / 3 % 4 - 5 + 6 > 0 OR ((a & 1) | (a << 2) | (a >> 3) | ~a) > 0 OR b || 'x' = 'yx' OR b -> '$.x' IS NOT NULL OR b ->> '$.y' IS NULL))" },
        { "numeric literals", "CREATE TABLE t (a REAL CHECK (a > -1.5e-3 AND a < .5 AND a <> 0x1F AND a <> 1E6 AND a <> 2.), b INTEGER DEFAULT 1_000)" },
        { "blob literals", "CREATE TABLE t (a BLOB CHECK (a <> X'00' AND a <> x''), b); INSERT INTO t VALUES (x'01', 1);" },
        { "CASE, IN, LIKE, GLOB and IS DISTINCT", "CREATE TABLE t (a, b, CHECK (CASE WHEN a IS NULL THEN 1 ELSE a BETWEEN 1 AND 9 END), CHECK (a IN (1, 2, 3) OR a LIKE 'x%' ESCAPE '\\' OR a GLOB '*' OR b NOT NULL OR a IS NOT DISTINCT FROM b)); INSERT INTO t VALUES (1, 2);" },
    };

    [Theory]
    [MemberData(nameof(Names))]
    [MemberData(nameof(Types))]
    [MemberData(nameof(ColumnConstraints))]
    [MemberData(nameof(TableConstraints))]
    [MemberData(nameof(TableOptions))]
    [MemberData(nameof(Tokens))]
    public void Schema(string name, string script)
    {
        _ = name;
        SqliteParity.AssertSchemaSameAsSqlite(script);
        SqliteParity.AssertSameAsSqlite(script);
    }
}
