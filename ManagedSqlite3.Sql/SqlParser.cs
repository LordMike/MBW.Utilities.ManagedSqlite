using System;
using System.Collections.Generic;
using System.Linq;
using ConsoleApp14;
using ManagedSqlite3.Sql.Internal;
using Superpower;
using Superpower.Model;

namespace ManagedSqlite3.Sql
{
    public static class SqlParser
    {
        public static bool TryParse(string sql, out SqlTableDefinition tableDefinition)
        {
            tableDefinition = null;

            Result<TokenList<SqlToken>> tokens = SqlTokenizer.Tokenizer.TryTokenize(sql);

            if (!tokens.HasValue)
                return false;

            TokenListParserResult<SqlToken, SqlCreateTableSchema> parseAttempt = SqlTokens.CreateTable.TryParse(tokens.Value);

            if (!parseAttempt.HasValue)
                return false;

            SqlCreateTableSchema parsed = parseAttempt.Value;
            tableDefinition = new SqlTableDefinition(parsed.TableName.Name);

            foreach (List<string>[] column in parsed.Columns)
            {
                List<string> first = column.First();

                bool isConstraint = SqlKeywords.ColumnKeywords.Contains(first.First());
                if (isConstraint)
                    continue;

                string name = first.First();
                string[] typeStrings = first.Skip(1).ToArray();

                Type identifiedType = typeof(byte[]);
                foreach ((string[] words, Type type) candidate in SqlKeywords.TypeKeywords)
                {
                    if (!typeStrings.Take(candidate.words.Length).SequenceEqual(candidate.words))
                        continue;

                    identifiedType = candidate.type;
                    break;
                }

                tableDefinition.Columns.Add((name, identifiedType));
            }

            return true;
        }
    }
}