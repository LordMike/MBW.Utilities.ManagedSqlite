using System;
using System.Collections.Generic;
using System.Linq;

namespace MBW.Utilities.ManagedSqlite.Sql.Internal
{
    internal static class SqlKeywords
    {
        public static HashSet<string> ColumnKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // column-constraint
            "CONSTRAINT", "PRIMARY", "NOT", "UNIQUE",
            "CHECK", "DEFAULT", "COLLATE",

            // foreign-key-clause
            "REFERENCES"
        };

        public static (string[] words, Type type)[] TypeKeywords =
            new[] {
                // integer, numeric
                (new[] { "INT", "INTEGER", "TINYINT", "SMALLINT", "MEDIUMINT", "BIGINT", "INT2", "INT8", "UNSIGNED BIG INT", "NUMERIC", "DECIMAL", "BOOLEAN", "DATE", "DATETIME" }, typeof(long)),

                // text
                (new[] { "CHARACTER", "VARCHAR", "VARYING CHARACTER","NCHAR", "NATIVE CHARACTER", "NVARCHAR", "TEXT", "CLOB", "LONGVARCHAR" }, typeof(string)),

                // blob
                (new[] { "BLOB" }, typeof(byte[])),

                // real
                (new[] { "REAL", "DOUBLE", "DOUBLE PRECISION", "FLOAT" }, typeof(double)),
            }.SelectMany(s => s.Item1.Select(x => (x.Split(' '), s.Item2))).ToArray();
    }
}