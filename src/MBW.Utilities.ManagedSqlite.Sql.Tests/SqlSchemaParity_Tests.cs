using MBW.Utilities.ManagedSqlite.Core.Tests.Helpers;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Sql.Tests;

/// <summary>
/// Each case creates tables with real SQLite and compares SQLite's view of their schema with the Sql package's parse
/// of the CREATE statements SQLite stored.
/// </summary>
public class SqlSchemaParity_Tests
{
    /// <summary>
    /// A comma separates column definitions and table constraints only at the top level of the column list. Every
    /// other comma, inside parentheses, literals, quoted names or comments, or between table options, must not.
    /// </summary>
    public static TheoryData<string, string> Commas() => new()
    {
        // An INTEGER PRIMARY KEY aliases the rowid only in a rowid table, so the alias shows whether WITHOUT ROWID was seen
        { "table options STRICT, WITHOUT ROWID", "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT) STRICT, WITHOUT ROWID" },
        { "table options WITHOUT ROWID, STRICT", "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT) WITHOUT ROWID, STRICT" },
        { "type sizes", "CREATE TABLE t (a DECIMAL(10, 5), b VARCHAR( 10 , 2 ), c TEXT)" },
        { "DEFAULT expressions", "CREATE TABLE t (a TEXT DEFAULT (printf('%d,%d', 1, 2)), b TEXT DEFAULT (replace('x,y', ',', ';')), c INTEGER)" },
        { "string literals", "CREATE TABLE t (a TEXT DEFAULT 'x, y', b TEXT DEFAULT ',' CHECK (b <> ', ('), c INTEGER)" },
        { "quoted names, also in PRIMARY KEY (a, b)", "CREATE TABLE \"t,1\" (\"a,b\" INTEGER, [x,y] TEXT, `p,q` REAL, 'r,s' BLOB, \"c\"\",\" TEXT, PRIMARY KEY (\"a,b\", [x,y]))" },
        { "CHECK with function calls, also as a trailing table constraint", "CREATE TABLE t (a TEXT CHECK (instr(a, ',') = 0 AND length(substr(a, 1, 2)) > 0), b INTEGER, CHECK (coalesce(a, b) IS NOT NULL))" },
        // An AS inside a CHECK does not make f a generated column
        { "generated columns", "CREATE TABLE t (a INTEGER, b INTEGER, c INTEGER AS (coalesce(a, b)), d INTEGER GENERATED ALWAYS AS (max(a, b)) STORED, e TEXT, f INTEGER CHECK (CAST(f AS TEXT) <> ''))" },
        { "UNIQUE (a, b)", "CREATE TABLE t (id INTEGER PRIMARY KEY, a INTEGER, b TEXT, UNIQUE (a, b))" },
        { "FOREIGN KEY (a, b) REFERENCES p(x, y)", @"
CREATE TABLE p (x INTEGER, y INTEGER, PRIMARY KEY (x, y));
CREATE TABLE t (id INTEGER PRIMARY KEY, a INTEGER, b INTEGER, FOREIGN KEY (a, b) REFERENCES p(x, y) ON DELETE CASCADE)" },
        { "CONSTRAINT name PRIMARY KEY (a, b)", "CREATE TABLE t (a INTEGER, b TEXT, c REAL, CONSTRAINT pk_t PRIMARY KEY (a, b))" },
        { "comments", "CREATE TABLE t (a INTEGER, -- a, b\n b TEXT /* , */, c /* ,, */ REAL -- ,\n)" },
        { "comments holding quotes and parentheses", "CREATE TABLE t (\r\n  a INTEGER, -- don't (count, this)\r\n  /* b TEXT, ( ' \" */ c TEXT -- ) closing paren is in a comment\r\n)" },
        { "newlines and odd spacing", "CREATE TABLE t (\n  a INTEGER\n  ,b TEXT ,c REAL\n,\n\td BLOB , e ,f\n)" },
    };

    /// <summary>
    /// Names in every quote style, with the quote character escaped by doubling it, and unquoted names. SQLite lets any
    /// character outside ASCII, '_' and '$' be part of an unquoted identifier, and only space, tab, line feed, form feed
    /// and carriage return are whitespace (a vertical tab only inside a run of it), so a no-break space joins the
    /// identifier it touches: a\u00A0INTEGER is one untyped column.
    /// </summary>
    public static TheoryData<string, string> Names() => new()
    {
        { "quoted names", "CREATE TABLE \"my\"\"table\" (\"\"\"\" TEXT, 'col''two' TEXT, `col``two` TEXT, [col two] TEXT, \"a'b`c\" TEXT, \"my\"\"id\" INTEGER, PRIMARY KEY (\"my\"\"id\"))" },
        { "unquoted names outside ASCII", "CREATE TABLE t (a€ INTEGER, b² TEXT, c😀 REAL, 日本 BLOB, _a$1 INTEGER)" },
        { "whitespace SQLite does not know", "CREATE TABLE t (a\u00A0INTEGER, \u000Bb\u000CTEXT)" },
    };

    /// <summary>
    /// Every operator and numeric literal form the tokenizer knows, in column and table CHECK constraints. A token it
    /// does not know makes the table unparseable.
    /// </summary>
    public static TheoryData<string, string> Expressions() => new()
    {
        { "comparison", "CREATE TABLE t (a INTEGER CHECK (a = 1 OR a == 1 OR a != 2 OR a <> 2 OR a < 9 OR a <= 9 OR a > 0 OR a >= 0), b TEXT, CHECK (a <> 3))" },
        { "arithmetic, bitwise, concatenation and JSON", "CREATE TABLE t (a INTEGER, b TEXT, CHECK (a * 2 / 3 % 4 - 5 + 6 > 0 OR ((a & 1) | (a << 2) | (a >> 3) | ~a) > 0 OR b || 'x' = 'yx' OR b -> '$.x' IS NOT NULL OR b ->> '$.y' IS NULL))" },
        { "numeric literals", "CREATE TABLE t (a REAL CHECK (a > -1.5e-3 AND a < .5 AND a <> 0x1F AND a <> 1E6 AND a <> 2.), b TEXT)" },
    };

    /// <summary>
    /// The declared type ends at the first column constraint, and leaves out its size
    /// </summary>
    public static TheoryData<string, string> Types() => new()
    {
        { "declared types", "CREATE TABLE t (a integer, b BIGINT PRIMARY KEY, c VARCHAR(10), d NVARCHAR(5) NOT NULL, e, f BLOB CONSTRAINT nn NOT NULL, g STRING)" },
    };

    [Theory]
    [MemberData(nameof(Types))]
    [MemberData(nameof(Commas))]
    [MemberData(nameof(Names))]
    [MemberData(nameof(Expressions))]
    public void Schema(string name, string script) => SqliteParity.AssertSchemaSameAsSqlite(script);
}
