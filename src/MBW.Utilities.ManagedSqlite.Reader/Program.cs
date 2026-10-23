using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommandLine;
using MBW.Utilities.ManagedSqlite.Core;
using MBW.Utilities.ManagedSqlite.Core.Tables;
using MBW.Utilities.ManagedSqlite.Sql;

namespace MBW.Utilities.ManagedSqlite.Reader;

class CommandBase
{
    [Value(0, Required = true, HelpText = "Database file")]
    public string DatabaseFile { get; set; }
}

[Verb("describe", HelpText = "Dumps the schema of an Sqlite3 database file.")]
class DescribeCommand : CommandBase
{
    [Option('s', "SizeStatistics", HelpText = "Calculate sizes of tables and indexes")]
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

    private static bool TryReadFile(string path, out Stream stream, out Sqlite3Database db, out List<Sqlite3SchemaRow> tables)
    {
        stream = null;
        db = null;
        tables = null;

        try
        {
            stream = File.OpenRead(path);
            db = Sqlite3Database.FromStreamWithNoJournal(stream);

            tables = db.GetTables().ToList();
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
            return false;
        }

        return true;
    }

    private static string InlineSql(string sql)
    {
        return sql.Replace("\r", "").Replace("\n", "").Replace("  ", "");
    }

    private static int RunDescribe(DescribeCommand opts)
    {
        if (!TryReadFile(opts.DatabaseFile, out Stream stream, out Sqlite3Database db,
                out List<Sqlite3SchemaRow> tables))
            return 1;

        Console.WriteLine($"Database: pagesize {db.Header.PageSize:N0}B, encoding {db.TextEncoding}");
        Console.WriteLine($"          size {db.Header.DatabaseSizeInPages:N0} pages/{db.Header.DatabaseSizeInPages * db.Header.PageSize:N0}B - actual size: {stream.Length:N0}B");
        Console.WriteLine();

        foreach (Sqlite3SchemaRow row in tables)
        {
            if (row.Type != "table")
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

            List<Sqlite3SchemaRow> indexes = tables.Where(s => s.Type == "index" && s.TableName == row.TableName).ToList();

            Console.WriteLine($"  Indexes ({indexes.Count})");
            foreach (Sqlite3SchemaRow index in indexes)
            {
                Console.WriteLine($"    {index.Name}");
                Console.WriteLine($"      Schema: {InlineSql(index.Sql)}");
            }

            if (!opts.DoSizeStatistics)
            {
                Console.WriteLine("Unable to do statistics");
            }

            Console.WriteLine();
        }

        return 0;
    }
}
