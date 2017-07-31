using System;
using System.IO;
using Sqlite3RoLib;

namespace Sqlite3Reader
{
    class Program
    {
        static void Main(string[] args)
        {
            using (FileStream fs = File.OpenRead("Db2.db"))
            using (Sqlite3Database db = new Sqlite3Database(fs))
            {
                var tables = db.GetTables();

                foreach (Sqlite3SchemaRow table in tables)
                {
                    Console.WriteLine(table.Name + ", " + table.TableName + ": " + table.Sql);
                }
            }

        }
    }
}