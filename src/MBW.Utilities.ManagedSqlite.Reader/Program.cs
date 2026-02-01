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

[Verb("dump", HelpText = "Dumps the contents of an Sqlite3 database file.")]
class DumpCommand : CommandBase
{

}

class Program
{
    static int Main(string[] args)
    {
        return Parser.Default.ParseArguments<DescribeCommand, DumpCommand>(args)
            .MapResult(
                (DescribeCommand opts) => RunDescribe(opts),
                (DumpCommand opts) => RunDump(opts),
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
            db = new Sqlite3Database(stream);

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

        Console.WriteLine($"Database: pagesize {db.Header.PageSize:N0}B, encoding {db.Header.TextEncoding}");
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

    private static int RunDump(DumpCommand opts)
    {
        if (!TryReadFile(opts.DatabaseFile, out Stream stream, out Sqlite3Database db,
                out List<Sqlite3SchemaRow> tables))
            return 1;

        throw new System.NotImplementedException();
    }

    //using (FileStream fs = File.OpenRead("BigBlobDb.db"))
    //using (Sqlite3Database db = new Sqlite3Database(fs))
    //{
    //    IEnumerable<Sqlite3SchemaRow> tables = db.GetTables();

    //    foreach (Sqlite3SchemaRow table in tables)
    //    {
    //        Console.WriteLine($"{table.Type} \"{table.Name}\", [{table.TableName}] (RP: {table.RootPage}): {table.Sql}; ");

    //        if (table.Type == "table")
    //        {
    //            Sqlite3Table tableData = db.GetTable(table.Name);

    //            foreach (Sqlite3Row row in tableData.EnumerateRows())
    //            {
    //                Console.Write(row.RowId);

    //                foreach (object obj in row.ColumnData)
    //                {
    //                    Console.Write(" | ");

    //                    if (obj == null)
    //                        Console.Write(" <null> ");
    //                    else
    //                        Console.Write(obj);
    //                }

    //                Console.WriteLine();
    //            }
    //        }
    //    }
    //}

}