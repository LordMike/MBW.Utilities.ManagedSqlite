using System;
using System.Collections.Generic;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Sql.Internal;
using Superpower;
using Superpower.Model;

namespace MBW.Utilities.ManagedSqlite.Sql
{
    public static class SqlParser
    {
        public static bool TryParse(string sql, out SqlTableDefinition tableDefinition)
        {
            tableDefinition = null;

            // Parse out the SQL into series of column creation statements
            Result<TokenList<SqlToken>> tokens = SqlTokenizer.Tokenizer.TryTokenize(sql);

            if (!tokens.HasValue)
                return false;

            TokenListParserResult<SqlToken, SqlCreateTableSchema> parseAttempt = SqlTokens.CreateTable.TryParse(tokens.Value);

            if (!parseAttempt.HasValue)
                return false;

            SqlCreateTableSchema parsed = parseAttempt.Value;
            tableDefinition = new SqlTableDefinition(parsed.TableName.Name);

            // Each column here is a 2d array of strings. The first dimension represents each "unbroken set of statements", while the second dimension is all those statements.
            // CREATE TABLE aa (a, b int, c int primary key) => 3 groups: ["a"], ["b", "int"], ["c", "int", "primary", "key"]
            int primaryKeyComponents = 0;
            SqlTableColumn primaryKeyColumn = null;

            for (int i = 0; i < parsed.Columns.Length; i++)
            {
                List<string>[] column = parsed.Columns[i];
                List<string> firstSet = column.First();

                bool isConstraint = SqlKeywords.ColumnKeywords.Contains(firstSet.First());
                if (isConstraint)
                {
                    if ("PRIMARY".Equals(firstSet.First(), StringComparison.OrdinalIgnoreCase) &&
                        "KEY".Equals(firstSet.Skip(1).First(), StringComparison.OrdinalIgnoreCase) &&
                        column.Skip(1).Any())
                    {
                        List<string> pkColumns = column[1];

                        foreach (string pkColumn in pkColumns)
                        {
                            if (!tableDefinition.TryGetColumn(pkColumn, out SqlTableColumn columnObject))
                                continue;

                            columnObject.IsPartOfPrimaryKey = true;

                            primaryKeyComponents++;
                            primaryKeyColumn = columnObject;
                        }
                    }

                    continue;
                }

                string name = firstSet.First();
                string[] typeStrings = firstSet.Skip(1).ToArray();

                Type identifiedType = typeof(byte[]);
                string[] identifiedTypeStrings = typeStrings;
                foreach ((string[] words, Type type) candidate in SqlKeywords.TypeKeywords)
                {
                    if (!typeStrings.Take(candidate.words.Length).SequenceEqual(candidate.words))
                        continue;

                    identifiedType = candidate.type;
                    identifiedTypeStrings = candidate.words;
                    break;
                }

                SqlTableColumn sqlTableColumn = new SqlTableColumn(i, name, string.Join(" ", identifiedTypeStrings), identifiedType);
                tableDefinition.AddColumn(sqlTableColumn);

                // Is this a primary key?
                if (typeStrings.Contains("PRIMARY", StringComparer.OrdinalIgnoreCase) &&
                    typeStrings.Contains("KEY", StringComparer.OrdinalIgnoreCase))
                {
                    sqlTableColumn.IsPartOfPrimaryKey = true;
                    primaryKeyComponents++;
                    primaryKeyColumn = sqlTableColumn;
                }
            }

            // Identify row-id substitutes
            if (primaryKeyComponents == 1 && primaryKeyColumn.DetectedType == typeof(long))
            {
                tableDefinition.ConfigureRowIdColumn(primaryKeyColumn);
            }

            return true;
        }
    }
}