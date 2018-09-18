using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace MBW.Utilities.ManagedSqlite.Sql.Tests
{
    public class SqlCreateStatements
    {
        public static IEnumerable<object[]> CreateStatementsTestsData()
        {
            Assembly assembly = typeof(SqlCreateStatements).Assembly;
            string[] files = {
                $"{assembly.GetName().Name}.Data.From_Chrome.txt",
                $"{assembly.GetName().Name}.Data.From_Firefox.txt",
                $"{assembly.GetName().Name}.Data.From_WindowsActivity.txt"
            };

            foreach (string file in files)
            {
                using (Stream fs = assembly.GetManifestResourceStream(file))
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

                        yield return new object[]{
                            sql,
                            name,
                            cols
                        };
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(CreateStatementsTestsData))]
        public void CreateStatementsTests(string sql, string tableName, List<ExpectedColumn> expectedColumns)
        {
            Assert.True(SqlParser.TryParse(sql, out SqlTableDefinition definition));
            Assert.NotNull(definition);

            Assert.Equal(tableName, definition.TableName);
            Assert.Equal(expectedColumns.Count, definition.Columns.Count);

            if (expectedColumns.All(s => !s.IsRowId))
                Assert.Null(definition.RowIdColumn);

            for (int i = 0; i < expectedColumns.Count; i++)
            {
                SqlTableColumn actual = definition.Columns.ElementAt(i);
                ExpectedColumn expected = expectedColumns[i];

                Assert.Equal(expected.ClrType, actual.DetectedType);
                Assert.Equal(expected.Name, actual.Name);
                Assert.Equal(expected.SqlType, actual.TypeName);
                Assert.Equal(expected.IsPrimaryKey, actual.IsPartOfPrimaryKey);

                if (expected.IsRowId)
                    Assert.Same(actual, definition.RowIdColumn);
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
