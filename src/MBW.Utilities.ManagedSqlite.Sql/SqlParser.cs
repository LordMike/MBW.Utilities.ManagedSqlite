using System;
using System.Collections.Generic;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Sql.Internal;
using Superpower.Model;

namespace MBW.Utilities.ManagedSqlite.Sql
{
    public static class SqlParser
    {
        private static bool TryParseTokenList(TokenList<SqlToken> list, out SqlNode rootNode)
        {
            rootNode = null;

            Stack<SqlNode> stack = new Stack<SqlNode>();
            stack.Push(new SqlNode());

            TokenListParserResult<SqlToken, Token<SqlToken>> tmp;
            while ((tmp = list.ConsumeToken()).HasValue)
            {
                list = tmp.Remainder;

                if (tmp.Value.Kind == SqlToken.Comma)
                {
                    if (stack.Count < 1)
                        return false;

                    stack.Pop();

                    SqlNode newNode = new SqlNode();
                    stack.Peek().AddChild(newNode);
                    stack.Push(newNode);

                    continue;
                }

                if (tmp.Value.Kind == SqlToken.ParenthesisStart)
                {
                    SqlNode newNode = new SqlNode();
                    SqlNode newInnerNode = new SqlNode();
                    newNode.AddChild(newInnerNode);

                    stack.Peek().AddChild(newNode);
                    stack.Push(newNode);
                    stack.Push(newInnerNode);
                    continue;
                }

                if (tmp.Value.Kind == SqlToken.ParenthesisEnd)
                {
                    if (stack.Count < 2)
                        return false;

                    stack.Pop();
                    stack.Pop();
                    continue;
                }

                stack.Peek().AddChild(new SqlNode(tmp.Value));
            }

            if (stack.Count != 1)
                return false;

            rootNode = stack.Pop();
            return true;
        }

        public static bool TryParse(string sql, out SqlTableDefinition tableDefinition)
        {
            tableDefinition = null;

            // Parse out the SQL into series of column creation statements
            Result<TokenList<SqlToken>> tokens = SqlTokenizer.Tokenizer.TryTokenize(sql);

            if (!tokens.HasValue)
                return false;

            if (!TryParseTokenList(tokens.Value, out SqlNode node))
                return false;

            // Check if this is a create table
            if (!node.Is(0, "CREATE") || !node.Is(1, "TABLE") || !node.Is(2, SqlToken.String) || !node.IsGroup(3))
                return false;

            SqlTableDefinition def = new SqlTableDefinition(node.GetString(2));

            SqlNode columns = node.GetGroup(3);
            int primaryKeyComponents = 0;
            SqlTableColumn primaryKeyColumn = null;

            for (int columnIndex = 0; columnIndex < columns.Children.Count; columnIndex++)
            {
                SqlNode columnSet = columns.Children[columnIndex];
                if (!columnSet.Children.Any() || !columnSet.Children.First().Is(SqlToken.String))
                    continue;

                string firstToken = columnSet.Children.First().GetString();

                bool isConstraint = SqlKeywords.NonColumnKeywords.Contains(firstToken);
                string name;
                if (isConstraint)
                {
                    if (columnSet.Is(0, "PRIMARY") && columnSet.Is(1, "KEY") && columnSet.IsGroup(2))
                    {
                        SqlNode pkColumns = columnSet.GetGroup(2);

                        foreach (SqlNode pkColumn in pkColumns.Children)
                        {
                            if (pkColumn.Is(SqlToken.String))
                                name = pkColumn.GetString();
                            else if (pkColumn.Is(0, SqlToken.String))
                                name = pkColumn.GetString(0);
                            else
                                return false;

                            if (!def.TryGetColumn(name, out SqlTableColumn columnObject))
                                continue;

                            columnObject.IsPartOfPrimaryKey = true;

                            primaryKeyComponents++;
                            primaryKeyColumn = columnObject;
                        }
                    }

                    continue;
                }

                name = columnSet.GetString(0);
                string[] typeStrings = columnSet.Children.Skip(1).Where(s => s.Is(SqlToken.String))
                    .Select(s => s.GetString()).ToArray();

                // Defaults
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

                SqlTableColumn sqlTableColumn = new SqlTableColumn(columnIndex, name, string.Join(" ", identifiedTypeStrings), identifiedType);
                def.AddColumn(sqlTableColumn);

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
                def.ConfigureRowIdColumn(primaryKeyColumn);
            }

            tableDefinition = def;
            return true;
        }
    }
}