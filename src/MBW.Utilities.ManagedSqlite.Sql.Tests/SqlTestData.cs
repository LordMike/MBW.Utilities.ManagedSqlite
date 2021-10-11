using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Sql.Tests
{
    public class SqlTestData
    {
        public List<ExpectedTable> Statements { get; }

        public SqlTestData(string resourceName)
        {
            Assembly assembly = typeof(SqlCreateStatements).Assembly;

            Statements = new List<ExpectedTable>();

            using (Stream fs = assembly.GetManifestResourceStream(resourceName))
            using (StreamReader sr = new StreamReader(fs))
            {
                // SQL
                //      TABLE-NAME
                //      ColumnName    ClrType SqlType Modifiers       <-- Columns
                //      ColumnName    ClrType SqlType Modifiers       <-- Columns
                //      ColumnName    ClrType SqlType Modifiers       <-- Columns
                //      ColumnName    ClrType SqlType Modifiers       <-- Columns

                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                        continue;

                    string sql = line;
                    string name = sr.ReadLine().Trim();

                    List<ExpectedColumn> cols = new List<ExpectedColumn>();
                    while (!string.IsNullOrWhiteSpace(line = sr.ReadLine()))
                    {
                        string[] parts = line.TrimStart().Split('\t');

                        string colName = parts[0];
                        string clrType = parts[1];
                        string sqlType = parts[2];
                        string modifiers = parts[3];

                        bool isPrimary = modifiers.Contains("PRIMARY");
                        bool isRowId = modifiers.Contains("ROWID");

                        cols.Add(Column(colName, sqlType, clrType, isPrimary, isRowId));
                    }

                    Statements.Add(new ExpectedTable
                    {
                        Sql = sql,
                        Name = name,
                        Columns = cols
                    });
                }
            }
        }

        static ExpectedColumn Column(string name, string sqlType, string clrType, bool isPrimaryKey = false, bool isRowId = false)
        {
            Type tp;
            switch (clrType)
            {
                case "INTEGER":
                    tp = typeof(long);
                    break;
                case "BYTES":
                    tp = typeof(byte[]);
                    break;
                case "STRING":
                    tp = typeof(string);
                    break;
                case "DOUBLE":
                    tp = typeof(double);
                    break;
                default:
                    throw new Exception($"Unsupported {clrType}");
            }

            return new ExpectedColumn(name, sqlType, tp, isPrimaryKey, isRowId);
        }

        public static void Compare(SqlTableDefinition definition, ExpectedTable expectedTable)
        {
            Assert.Equal(expectedTable.Name, definition.TableName);
            Assert.Equal(expectedTable.Columns.Count, definition.Columns.Count);

            if (expectedTable.Columns.All(s => !s.IsRowId))
                Assert.Null(definition.RowIdColumn);

            for (int i = 0; i < expectedTable.Columns.Count; i++)
            {
                SqlTableColumn actual = definition.Columns.ElementAt(i);
                ExpectedColumn expected = expectedTable.Columns[i];

                Assert.Equal(expected.ClrType, actual.DetectedType);
                Assert.Equal(expected.Name, actual.Name);
                Assert.Equal(expected.SqlType, actual.TypeName);
                Assert.Equal(expected.IsPrimaryKey, actual.IsPartOfPrimaryKey);

                if (expected.IsRowId)
                    Assert.Same(actual, definition.RowIdColumn);
            }
        }

        public static void AppendActual(StreamWriter sw, string sql, SqlTableDefinition definition)
        {
            sw.WriteLine(sql);
            sw.WriteLine("\t" + definition.TableName);



            foreach (var sqlTableColumn in definition.Columns)
            {
                sw.Write("\t");
                sw.Write(sqlTableColumn.Name);
                sw.Write("\t");
                switch (sqlTableColumn.DetectedType.Name)
                {
                    case "Int64":
                        sw.Write("INTEGER");
                        break;
                    case "Byte[]":
                        sw.Write("BYTES");
                        break;
                    case "String":
                        sw.Write("STRING");
                        break;
                    case "Double":
                        sw.Write("DOUBLE");
                        break;
                    default:
                        throw new Exception();
                }

                sw.Write("\t");
                sw.Write(sqlTableColumn.TypeName);
                sw.Write("\t");
                if (sqlTableColumn.IsPartOfPrimaryKey)
                    sw.Write("PRIMARY");

                if (definition.RowIdColumn.Name == sqlTableColumn.Name)
                    sw.Write(" ROWID");

                sw.WriteLine();
            }

            sw.WriteLine();
        }

        public class ExpectedTable
        {
            public string Sql { get; set; }

            public string Name { get; set; }

            public List<ExpectedColumn> Columns { get; set; }
        }

        public class ExpectedColumn
        {
            public string Name { get; }
            public string SqlType { get; }
            public Type ClrType { get; }
            public bool IsPrimaryKey { get; }
            public bool IsRowId { get; }

            public ExpectedColumn(string name, string sqlType, Type clrType, bool isPrimaryKey, bool isRowId)
            {
                Name = name;
                SqlType = sqlType;
                ClrType = clrType;
                IsPrimaryKey = isPrimaryKey;
                IsRowId = isRowId;
            }
        }
    }
}