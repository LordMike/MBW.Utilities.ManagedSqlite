using System.IO;
using Sqlite3RoLib;

namespace Sqlite3Reader
{
    class Program
    {
        static void Main(string[] args)
        {
            using (FileStream fs = File.OpenRead("Db1.db"))
            using (Sqlite3Database db = new Sqlite3Database(fs))
            {

            }

        }
    }
}