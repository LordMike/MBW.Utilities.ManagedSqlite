using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Differential.Tests.Helpers;
using MBW.Utilities.ManagedSqlite.Sql;
using Xunit;
using Xunit.Sdk;

namespace MBW.Utilities.ManagedSqlite.Differential.Tests;

/// <summary>
/// Differential tests for table names and column definitions. Each case is a script that creates one table (usually
/// with one column) and inserts one row, plus the name a caller would look the table up by. The library must list
/// the table, find it by that name, and return exactly SQLite's row, both through ColumnData and through the Sql
/// package's TryGetValueByName.
/// </summary>
/// <remarks>
/// Some cases fail on master on purpose; they document known bugs and turn green as the fixes land.
/// </remarks>
public class TableSchema_Tests
{
    public static TheoryData<string, string> Scripts()
    {
        TheoryData<string, string> data = new TheoryData<string, string>();

        // Table names
        data.Add("plain", "CREATE TABLE plain(a); INSERT INTO plain VALUES (42);");
        data.Add("quoted", "CREATE TABLE \"quoted\"(a); INSERT INTO \"quoted\" VALUES (42);");
        data.Add("bracketed", "CREATE TABLE [bracketed](a); INSERT INTO [bracketed] VALUES (42);");
        data.Add("backticked", "CREATE TABLE `backticked`(a); INSERT INTO `backticked` VALUES (42);");
        data.Add("with space", "CREATE TABLE \"with space\"(a); INSERT INTO \"with space\" VALUES (42);");
        data.Add("bracketed space", "CREATE TABLE [bracketed space](a); INSERT INTO [bracketed space] VALUES (42);");
        data.Add("tæst_ø_日本_😀", "CREATE TABLE \"tæst_ø_日本_😀\"(a); INSERT INTO \"tæst_ø_日本_😀\" VALUES (42);");
        data.Add("unquotedæøå", "CREATE TABLE unquotedæøå(a); INSERT INTO unquotedæøå VALUES (42);");
        data.Add("select", "CREATE TABLE \"select\"(a); INSERT INTO \"select\" VALUES (42);");
        data.Add("order", "CREATE TABLE [order](a); INSERT INTO [order] VALUES (42);");
        data.Add("table", "CREATE TABLE `table`(a); INSERT INTO `table` VALUES (42);");
        data.Add("we\"ird", "CREATE TABLE \"we\"\"ird\"(a); INSERT INTO \"we\"\"ird\" VALUES (42);");
        data.Add("MixedCase", "CREATE TABLE MixedCase(a); INSERT INTO MixedCase VALUES (42);");
        data.Add("mixedcase_lookup", "CREATE TABLE MixedCase_Lookup(a); INSERT INTO MixedCase_Lookup VALUES (42);");
        data.Add("UPPERCASE_LOOKUP", "CREATE TABLE uppercase_lookup(a); INSERT INTO uppercase_lookup VALUES (42);");
        data.Add("qualified", "CREATE TABLE main.qualified(a); INSERT INTO qualified VALUES (42);");
        data.Add("quoted_qualified", "CREATE TABLE \"main\".\"quoted_qualified\"(a); INSERT INTO quoted_qualified VALUES (42);");
        data.Add("if_not_exists", "CREATE TABLE IF NOT EXISTS if_not_exists(a); INSERT INTO if_not_exists VALUES (42);");
        data.Add("lowercase_create", "create table lowercase_create(a); insert into lowercase_create values (42);");
        data.Add("whitespace", "CREATE   TABLE\n\twhitespace\n(\n  a\n)\n; INSERT INTO whitespace VALUES (42);");
        data.Add("second_of_two", "CREATE TABLE first_of_two(x); INSERT INTO first_of_two VALUES ('no'); CREATE TABLE second_of_two(a); INSERT INTO second_of_two VALUES (42);");
        data.Add("with_index", "CREATE TABLE with_index(a); CREATE INDEX with_index_a ON with_index(a); INSERT INTO with_index VALUES (42);");

        // Column types
        data.Add("type_none", "CREATE TABLE type_none(a); INSERT INTO type_none VALUES ('text');");
        data.Add("type_varchar", "CREATE TABLE type_varchar(a VARCHAR(255)); INSERT INTO type_varchar VALUES ('text');");
        data.Add("type_decimal", "CREATE TABLE type_decimal(a DECIMAL(10, 5)); INSERT INTO type_decimal VALUES (12.5);");
        data.Add("type_double_precision", "CREATE TABLE type_double_precision(a DOUBLE PRECISION); INSERT INTO type_double_precision VALUES (12.5);");
        data.Add("type_unsigned_big_int", "CREATE TABLE type_unsigned_big_int(a UNSIGNED BIG INT); INSERT INTO type_unsigned_big_int VALUES (42);");
        data.Add("type_lowercase_int", "CREATE TABLE type_lowercase_int(a int); INSERT INTO type_lowercase_int VALUES (42);");
        data.Add("type_lowercase_text", "CREATE TABLE type_lowercase_text(a text); INSERT INTO type_lowercase_text VALUES ('text');");
        data.Add("type_string", "CREATE TABLE type_string(a STRING); INSERT INTO type_string VALUES ('text');");
        data.Add("type_floating_point", "CREATE TABLE type_floating_point(a FLOATING POINT); INSERT INTO type_floating_point VALUES (42);");
        data.Add("type_quoted", "CREATE TABLE type_quoted(a \"my type\"); INSERT INTO type_quoted VALUES ('text');");
        data.Add("type_datetime", "CREATE TABLE type_datetime(a DATETIME); INSERT INTO type_datetime VALUES ('2026-10-19 12:00:00');");
        data.Add("type_blob", "CREATE TABLE type_blob(a BLOB); INSERT INTO type_blob VALUES (X'CAFE');");

        // Column names
        data.Add("col_quoted_space", "CREATE TABLE col_quoted_space(\"my col\" TEXT); INSERT INTO col_quoted_space VALUES ('text');");
        data.Add("col_bracketed", "CREATE TABLE col_bracketed([my col] TEXT); INSERT INTO col_bracketed VALUES ('text');");
        data.Add("col_backticked", "CREATE TABLE col_backticked(`my col` TEXT); INSERT INTO col_backticked VALUES ('text');");
        data.Add("col_keyword", "CREATE TABLE col_keyword(\"select\" TEXT, [order] INTEGER); INSERT INTO col_keyword VALUES ('text', 42);");
        data.Add("col_unicode", "CREATE TABLE col_unicode(\"kølønne_日本\" TEXT); INSERT INTO col_unicode VALUES ('text');");
        data.Add("col_escaped_quote", "CREATE TABLE col_escaped_quote(\"col\"\"two\" TEXT); INSERT INTO col_escaped_quote VALUES ('text');");
        data.Add("col_mixed_case", "CREATE TABLE col_mixed_case(MyColumn TEXT); INSERT INTO col_mixed_case VALUES ('text');");

        // Constraints, defaults, comments
        data.Add("not_null_unique", "CREATE TABLE not_null_unique(a TEXT NOT NULL UNIQUE); INSERT INTO not_null_unique VALUES ('text');");
        data.Add("default_number", "CREATE TABLE default_number(a INTEGER DEFAULT 5, b TEXT); INSERT INTO default_number(b) VALUES ('x');");
        data.Add("default_negative", "CREATE TABLE default_negative(a INTEGER DEFAULT -5, b TEXT); INSERT INTO default_negative(b) VALUES ('x');");
        data.Add("default_string", "CREATE TABLE default_string(a TEXT DEFAULT 'dflt', b TEXT); INSERT INTO default_string(b) VALUES ('x');");
        data.Add("default_expression", "CREATE TABLE default_expression(a INTEGER DEFAULT (40 + 2), b TEXT); INSERT INTO default_expression(b) VALUES ('x');");
        data.Add("default_current_timestamp", "CREATE TABLE default_current_timestamp(a TEXT DEFAULT CURRENT_TIMESTAMP, b TEXT); INSERT INTO default_current_timestamp VALUES ('2026-10-19', 'x');");
        data.Add("collate_nocase", "CREATE TABLE collate_nocase(a TEXT COLLATE NOCASE); INSERT INTO collate_nocase VALUES ('text');");
        data.Add("references", "CREATE TABLE parent(id INTEGER PRIMARY KEY); CREATE TABLE \"references\"(a INTEGER REFERENCES parent(id) ON DELETE CASCADE); INSERT INTO parent VALUES (42); INSERT INTO \"references\" VALUES (42);");
        data.Add("check_column", "CREATE TABLE check_column(a INTEGER CHECK (a > 0)); INSERT INTO check_column VALUES (42);");
        data.Add("check_table", "CREATE TABLE check_table(a INTEGER, b TEXT, CHECK (a > 0 AND length(b) < 10)); INSERT INTO check_table VALUES (42, 'x');");
        data.Add("named_constraint", "CREATE TABLE named_constraint(a INTEGER CONSTRAINT positive CHECK (a > 0)); INSERT INTO named_constraint VALUES (42);");
        data.Add("unique_table_constraint", "CREATE TABLE unique_table_constraint(a TEXT, b TEXT, UNIQUE (a, b)); INSERT INTO unique_table_constraint VALUES ('x', 'y');");
        data.Add("line_comment", "CREATE TABLE line_comment(\n  a TEXT -- the only column\n); INSERT INTO line_comment VALUES ('text');");
        data.Add("block_comment", "CREATE TABLE block_comment(a /* untyped */ TEXT); INSERT INTO block_comment VALUES ('text');");
        data.Add("strict", "CREATE TABLE strict(a INTEGER, b TEXT) STRICT; INSERT INTO strict VALUES (42, 'x');");

        // Rowid aliases: id 42 inserted, so an alias shows 42 and a non-alias shows rowid 1 vs value 42
        data.Add("ipk_upper", "CREATE TABLE ipk_upper(id INTEGER PRIMARY KEY, v TEXT); INSERT INTO ipk_upper VALUES (42, 'x');");
        data.Add("ipk_lower", "CREATE TABLE ipk_lower(id integer primary key, v text); INSERT INTO ipk_lower VALUES (42, 'x');");
        data.Add("ipk_mixed", "CREATE TABLE ipk_mixed(id Integer Primary Key, v Text); INSERT INTO ipk_mixed VALUES (42, 'x');");
        data.Add("ipk_autoincrement", "CREATE TABLE ipk_autoincrement(id INTEGER PRIMARY KEY AUTOINCREMENT, v TEXT); INSERT INTO ipk_autoincrement VALUES (42, 'x');");
        data.Add("ipk_not_null", "CREATE TABLE ipk_not_null(id INTEGER NOT NULL PRIMARY KEY, v TEXT); INSERT INTO ipk_not_null VALUES (42, 'x');");
        data.Add("ipk_table_constraint", "CREATE TABLE ipk_table_constraint(id INTEGER, v TEXT, PRIMARY KEY (id)); INSERT INTO ipk_table_constraint VALUES (42, 'x');");
        data.Add("ipk_not_first", "CREATE TABLE ipk_not_first(v TEXT, id INTEGER PRIMARY KEY); INSERT INTO ipk_not_first VALUES ('x', 42);");
        data.Add("ipk_desc_not_alias", "CREATE TABLE ipk_desc_not_alias(id INTEGER PRIMARY KEY DESC, v TEXT); INSERT INTO ipk_desc_not_alias VALUES (42, 'x');");
        data.Add("int_pk_not_alias", "CREATE TABLE int_pk_not_alias(id INT PRIMARY KEY, v TEXT); INSERT INTO int_pk_not_alias VALUES (42, 'x');");
        data.Add("bigint_pk_not_alias", "CREATE TABLE bigint_pk_not_alias(id BIGINT PRIMARY KEY, v TEXT); INSERT INTO bigint_pk_not_alias VALUES (42, 'x');");
        data.Add("composite_pk_not_alias", "CREATE TABLE composite_pk_not_alias(a INTEGER, b INTEGER, PRIMARY KEY (a, b)); INSERT INTO composite_pk_not_alias VALUES (42, 43);");
        data.Add("text_pk", "CREATE TABLE text_pk(k TEXT PRIMARY KEY, v TEXT); INSERT INTO text_pk VALUES ('key', 'x');");

        // Table kinds
        data.Add("without_rowid", "CREATE TABLE without_rowid(k TEXT PRIMARY KEY, v) WITHOUT ROWID; INSERT INTO without_rowid VALUES ('key', 42);");
        data.Add("fts5_table", "CREATE VIRTUAL TABLE fts5_table USING fts5(a); INSERT INTO fts5_table VALUES ('hello world');");
        data.Add("generated_virtual", "CREATE TABLE generated_virtual(a INTEGER, b INTEGER AS (a * 2) VIRTUAL, c TEXT); INSERT INTO generated_virtual(a, c) VALUES (21, 'x');");
        data.Add("generated_stored", "CREATE TABLE generated_stored(a INTEGER, b INTEGER GENERATED ALWAYS AS (a * 2) STORED, c TEXT); INSERT INTO generated_stored(a, c) VALUES (21, 'x');");

        return data;
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public Task LibraryFindsTableAndReturnsSameRow(string lookupName, string script)
    {
        return CaseTimeout.Run(() => DifferentialRunner.Run(script, lookupName, (expected, db) => AssertTable(expected, db, lookupName)), lookupName);
    }

    private static void AssertTable(ExpectedTable expected, Sqlite3Database db, string lookupName)
    {
        // Listed under its stored name
        List<Sqlite3SchemaRow> tables = db.GetTables().ToList();
        if (!tables.Any(s => s.Type == "table" && s.Name == expected.Name))
            throw new XunitException($"GetTables() does not list table '{expected.Name}'. It lists: " +
                                     string.Join(", ", tables.Select(s => $"{s.Type} '{s.Name}'")));

        // Found by the name SQLite resolves (case-insensitively)
        Assert.True(db.TryGetTable(lookupName, out Sqlite3Table table), $"TryGetTable(\"{lookupName}\") returned false; SQLite resolves it to '{expected.Name}'");

        List<Sqlite3Row> rows = table.EnumerateRows().ToList();

        // Raw record values
        RowAssert.Equal(expected, rows);

        // Values by column name through the Sql package, where the rowid alias resolves to the rowid
        for (int r = 0; r < rows.Count; r++)
        {
            for (int c = 0; c < expected.ColumnNames.Length; c++)
            {
                string column = expected.ColumnNames[c];
                object expectedValue = expected.Rows[r].Values[c];

                Assert.True(rows[r].TryGetValueByName(column, out object actual), $"TryGetValueByName(\"{column}\") returned false");

                if (!RowAssert.ValueEquals(expectedValue, actual))
                    throw new XunitException($"TryGetValueByName(\"{column}\") on row {r}: expected {RowAssert.Describe(expectedValue)}, actual {RowAssert.Describe(actual)}");
            }
        }
    }
}
