using System;
using System.Collections.Generic;
using System.IO;
using ManagedSqlite3.Core;
using ManagedSqlite3.Core.Tables;

namespace ManagedSqlite3.Reader
{
    class Program
    {
        static void Main(string[] args)
        {
            using (FileStream fs = File.OpenRead("BigBlobDb.db"))
            using (Sqlite3Database db = new Sqlite3Database(fs))
            {
                IEnumerable<Sqlite3SchemaRow> tables = db.GetTables();

                foreach (Sqlite3SchemaRow table in tables)
                {
                    Console.WriteLine($"{table.Type} \"{table.Name}\", [{table.TableName}] (RP: {table.RootPage}): {table.Sql}; ");

                    if (table.Type == "table")
                    {
                        Sqlite3Table tableData = db.GetTable(table.Name);
                         
                        foreach (Sqlite3Row row in tableData.EnumerateRows())
                        {
                            Console.Write(row.RowId);

                            foreach (object obj in row.ColumnData)
                            {
                                Console.Write(" | ");

                                if (obj == null)
                                    Console.Write(" <null> ");
                                else
                                    Console.Write(obj);
                            }

                            Console.WriteLine();
                        }
                    }
                }
            }
        }
    }
}