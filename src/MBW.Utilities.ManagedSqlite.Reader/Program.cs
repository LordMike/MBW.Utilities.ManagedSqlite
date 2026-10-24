using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using CommandLine;
using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Sql;

namespace MBW.Utilities.ManagedSqlite.Reader;

[Verb("describe", HelpText = "Dumps the schema of an Sqlite3 database file. A -wal or -journal file next to it is used.")]
class DescribeCommand
{
    [Value(0, Required = true, HelpText = "Database file")]
    public string DatabaseFile { get; set; } = null!;

    [Option('s', "SizeStatistics", HelpText = "Calculate sizes of tables and indexes (not implemented)")]
    public bool DoSizeStatistics { get; set; }
}

class Program
{
    static int Main(string[] args)
    {
        return Parser.Default.ParseArguments(args, typeof(DescribeCommand))
            .MapResult(
                (DescribeCommand opts) => RunDescribe(opts),
                errs => 1);
    }

    private static bool TryReadFile(string path, [NotNullWhen(true)] out Sqlite3Database? db, [NotNullWhen(true)] out List<Sqlite3SchemaRow>? tables)
    {
        db = null;
        tables = null;

        try
        {
            db = Sqlite3Database.FromPath(path);

            tables = db.GetSchema().ToList();
        }
        catch (Exception e)
        {
            db?.Dispose();
            db = null;

            Console.Error.WriteLine(e.Message);
            return false;
        }

        return true;
    }

    private static string InlineSql(string? sql)
    {
        return (sql ?? "").Replace("\r", "").Replace("\n", "").Replace("  ", "");
    }

    private static int RunDescribe(DescribeCommand opts)
    {
        if (!TryReadFile(opts.DatabaseFile, out Sqlite3Database? db, out List<Sqlite3SchemaRow>? tables))
            return 1;

        Console.WriteLine($"Database: encoding {db.TextEncoding}, WAL mode {(db.IsWalMode ? "yes" : "no")}, size {new FileInfo(opts.DatabaseFile).Length:N0}B");
        Console.WriteLine();

        if (opts.DoSizeStatistics)
        {
            Console.Error.WriteLine("Unable to do statistics: not implemented");
            Console.Error.WriteLine();
        }

        foreach (Sqlite3SchemaRow row in tables)
        {
            if (row.Kind is not (Sqlite3SchemaKind.Table or Sqlite3SchemaKind.VirtualTable))
                continue;

            Console.WriteLine($"{row.Type} {row.Name}");
            Console.WriteLine($"  Schema: {InlineSql(row.Sql)}");
            Console.WriteLine($"  Root Page: {row.RootPage}");

            try
            {
                SqlTableDefinition definition = row.GetTableDefinition();

                Console.WriteLine($"  Columns ({definition.Columns.Count})");
                foreach (SqlTableColumn column in definition.Columns)
                {
                    Console.WriteLine($"    {column.Name} ({column.TypeName}) => {column.DetectedType.Name}");
                }
            }
            catch (Exception)
            {
                Console.WriteLine("  Error: Unable to parse SQL");
            }

            List<Sqlite3SchemaRow> indexes = tables.Where(s => s.Kind == Sqlite3SchemaKind.Index && s.TableName == row.TableName).ToList();

            Console.WriteLine($"  Indexes ({indexes.Count})");
            foreach (Sqlite3SchemaRow index in indexes)
            {
                Console.WriteLine($"    {index.Name}");
                Console.WriteLine($"      Schema: {InlineSql(index.Sql)}");
            }

            Console.WriteLine();
        }

        db.Dispose();
        return 0;
    }
}
