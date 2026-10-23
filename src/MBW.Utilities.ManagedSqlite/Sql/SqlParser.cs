using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Exceptions;
using MBW.Utilities.ManagedSqlite.Core.Objects.Enums;
using MBW.Utilities.ManagedSqlite.Sql.Internal.Grammar;

namespace MBW.Utilities.ManagedSqlite.Sql;

/// <summary>
/// Reads a table's CREATE TABLE statement as SQLite does when it loads the schema (<see cref="SchemaParser"/>), and
/// describes the table's columns from what SQLite builds
/// </summary>
internal static class SqlParser
{
    /// <summary>
    /// Parses the CREATE TABLE statement of the schema row <paramref name="objectName"/>, as in a UTF-8 database
    /// </summary>
    /// <exception cref="SqliteCorruptException">SQLite would find the statement malformed</exception>
    public static SqlTableDefinition Parse(string sql, string objectName, Sqlite3Settings settings)
    {
        return Build(SchemaParser.ParseCreateTable(sql, objectName, settings), SqliteEncoding.UTF8);
    }

    /// <summary>
    /// Parses a CREATE TABLE statement in <see cref="SqliteValidation.Strict"/>, false when SQLite would refuse it
    /// </summary>
    public static bool TryParse(string? sql, [NotNullWhen(true)] out SqlTableDefinition? tableDefinition)
    {
        tableDefinition = null;
        if (sql == null)
            return false;

        try
        {
            tableDefinition = Parse(sql, "", new Sqlite3Settings());
            return true;
        }
        catch (SqliteCorruptException)
        {
            return false;
        }
    }

    /// <summary>
    /// Describes a table SQLite built from its CREATE TABLE, in a database of text encoding <paramref name="encoding"/>
    /// </summary>
    public static SqlTableDefinition Build(ParsedTable table, SqliteEncoding encoding)
    {
        SqlTableDefinition definition = new SqlTableDefinition(table.Name);
        definition.IsWithoutRowId = table.Has(ParsedTable.TF_WithoutRowid);

        int virtualColumns = 0;
        for (int i = 0; i < table.Columns.Count; i++)
        {
            ParsedColumn parsed = table.Columns[i];
            SqlTableColumn column = new SqlTableColumn(i, parsed.Name, TypeName(parsed), DetectedType(parsed.Affinity))
            {
                Collation = parsed.Collation,
                IsPartOfPrimaryKey = parsed.Has(ParsedColumn.COLFLAG_PRIMKEY),
                IsVirtualGenerated = parsed.Has(ParsedColumn.COLFLAG_VIRTUAL)
            };

            // A rowid table's record holds every column but the VIRTUAL generated ones, in declared order. A WITHOUT
            // ROWID table's holds the key columns first (convertToWithoutRowidTable in build.c), and a key column
            // that is in the key twice, with two collations, is read from the first.
            if (definition.IsWithoutRowId)
                column.RecordIndex = table.RecordColumns.IndexOf(i);
            else
                column.RecordIndex = i - virtualColumns;

            if (column.IsVirtualGenerated)
                virtualColumns++;
            else
            {
                column.HasKnownDefault = ColumnDefault.TryEvaluate(parsed.Default, parsed.Affinity, encoding, out object? value);
                column.DefaultValue = value;
            }

            definition.AddColumn(column);
        }

        // iPKey, the INTEGER PRIMARY KEY that is the rowid; a WITHOUT ROWID table has none
        if (table.PrimaryKeyColumn >= 0)
            definition.ConfigureRowIdColumn(definition.Columns.ElementAt(table.PrimaryKeyColumn));

        return definition;
    }

    /// <summary>
    /// The declared type as SQLite reports it (sqlite3ColumnType in util.c: the dequoted type text, or the standard
    /// type's name, such as "INTEGER", which SQLite keeps instead of the text), without a size such as "(10, 5)" at
    /// the end, and with each run of white space as one space
    /// </summary>
    private static string TypeName(ParsedColumn column)
    {
        string type;
        if (column.Has(ParsedColumn.COLFLAG_HASTYPE))
            type = column.Type!;
        else if (column.StandardType != ParsedColumn.COLTYPE_CUSTOM)
            type = new[] { "ANY", "BLOB", "INT", "INTEGER", "REAL", "TEXT" }[column.StandardType - 1];
        else
            return "";

        // The size: from the first "(" that has no line break between it and a ")" that ends the text
        int close = type.EndsWith(")", StringComparison.Ordinal) ? type.Length - 1 :
            type.EndsWith(")\n", StringComparison.Ordinal) ? type.Length - 2 : -1;
        int open = -1;
        for (int i = close; i >= 0 && type[i] != '\n'; i--)
        {
            if (type[i] == '(')
                open = i;
        }

        if (open >= 0)
            type = type.Substring(0, open);

        type = type.Trim();
        StringBuilder sb = new StringBuilder(type.Length);
        for (int i = 0; i < type.Length; i++)
        {
            char c = type[i];
            if (!char.IsWhiteSpace(c))
                sb.Append(c);
            else if (sb[sb.Length - 1] != ' ')
                sb.Append(' ');
        }

        return sb.ToString();
    }

    /// <summary>
    /// The CLR type of the column's affinity. NUMERIC is reported as long, although SQLite may store a real, text or
    /// blob there.
    /// </summary>
    private static Type DetectedType(char affinity) => affinity switch
    {
        ParsedColumn.AFF_TEXT => typeof(string),
        ParsedColumn.AFF_BLOB => typeof(byte[]),
        ParsedColumn.AFF_REAL => typeof(double),
        _ => typeof(long)
    };
}
