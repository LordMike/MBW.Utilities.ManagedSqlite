using System;
using System.Collections.Generic;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Sql.Internal;
using Superpower.Model;

namespace MBW.Utilities.ManagedSqlite.Sql;

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
                // Commas are only meaningful inside parentheses
                if (stack.Count < 2)
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

        if (sql == null)
            return false;

        try
        {
            return TryParseInternal(sql, out tableDefinition);
        }
        catch (Exception)
        {
            // Anything unexpected in the SQL means it could not be parsed
            tableDefinition = null;
            return false;
        }
    }

    private static bool TryParseInternal(string sql, out SqlTableDefinition tableDefinition)
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

        // Columns that may alias the rowid if they turn out to be the sole primary key
        HashSet<SqlTableColumn> rowIdAliasCandidates = new HashSet<SqlTableColumn>();

        // VIRTUAL generated columns are not stored, so each one moves the later columns one position left in the record
        int virtualGeneratedColumns = 0;

        for (int columnIndex = 0; columnIndex < columns.Children.Count; columnIndex++)
        {
            SqlNode columnSet = columns.Children[columnIndex];
            if (columnSet.Children == null || !columnSet.Children.Any() || !columnSet.Children.First().Is(SqlToken.String))
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

            // Duplicate column names are invalid
            if (def.TryGetColumn(name, out _))
                return false;

            string[] typeStrings = columnSet.Children.Skip(1).Where(s => s.Is(SqlToken.String))
                .Select(s => s.GetString()).ToArray();

            // Defaults
            Type identifiedType = typeof(byte[]);
            string[] identifiedTypeStrings = typeStrings;

            foreach ((string[] words, Type type) candidate in SqlKeywords.TypeKeywords)
            {
                if (!typeStrings.Take(candidate.words.Length).SequenceEqual(candidate.words, StringComparer.OrdinalIgnoreCase))
                    continue;

                identifiedType = candidate.type;
                identifiedTypeStrings = candidate.words;
                break;
            }

            SqlTableColumn sqlTableColumn = new SqlTableColumn(columnIndex, name, string.Join(" ", identifiedTypeStrings), identifiedType);
            def.AddColumn(sqlTableColumn);

            sqlTableColumn.RecordIndex = columnIndex - virtualGeneratedColumns;
            if (IsVirtualGenerated(columnSet))
            {
                sqlTableColumn.IsVirtualGenerated = true;
                virtualGeneratedColumns++;
            }

            // Is this a primary key?
            if (typeStrings.Contains("PRIMARY", StringComparer.OrdinalIgnoreCase) &&
                typeStrings.Contains("KEY", StringComparer.OrdinalIgnoreCase))
            {
                sqlTableColumn.IsPartOfPrimaryKey = true;
                primaryKeyComponents++;
                primaryKeyColumn = sqlTableColumn;
            }

            // A column declared as exactly "INTEGER" may alias the rowid. As a quirk, SQLite does not
            // alias the rowid when the column-constraint is "PRIMARY KEY DESC" (a table-constraint
            // "PRIMARY KEY (x DESC)" does alias it). https://www.sqlite.org/lang_createtable.html#rowid
            if (IsDeclaredAsInteger(columnSet) && !IsColumnPrimaryKeyDesc(columnSet))
                rowIdAliasCandidates.Add(sqlTableColumn);
        }

        // Identify row-id substitutes
        if (primaryKeyComponents == 1 && rowIdAliasCandidates.Contains(primaryKeyColumn) && !IsWithoutRowId(node))
        {
            def.ConfigureRowIdColumn(primaryKeyColumn);
        }

        tableDefinition = def;
        return true;
    }

    private static bool IsDeclaredAsInteger(SqlNode columnSet)
    {
        // The declared type is everything between the column name and the first column-constraint
        List<SqlNode> declaredType = columnSet.Children.Skip(1)
            .TakeWhile(s => !(s.Is(SqlToken.String) && SqlKeywords.ColumnConstraintKeywords.Contains(s.GetString())))
            .ToList();

        return declaredType.Count == 1 &&
               declaredType[0].Is(SqlToken.String) &&
               string.Equals(declaredType[0].GetString().Trim('"', '[', ']', '`', '\''), "INTEGER", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A generated column is "[GENERATED ALWAYS] AS (expr) [VIRTUAL | STORED]", and is VIRTUAL unless STORED is given.
    /// https://www.sqlite.org/gencol.html
    /// </summary>
    private static bool IsVirtualGenerated(SqlNode columnSet)
    {
        for (int i = 1; i < columnSet.Children.Count; i++)
        {
            if (columnSet.Is(i, "AS") && columnSet.IsGroup(i + 1))
                return !columnSet.Is(i + 2, "STORED");
        }

        return false;
    }

    private static bool IsColumnPrimaryKeyDesc(SqlNode columnSet)
    {
        for (int i = 1; i < columnSet.Children.Count; i++)
        {
            if (columnSet.Is(i, "PRIMARY") && columnSet.Is(i + 1, "KEY"))
                return columnSet.Is(i + 2, "DESC");
        }

        return false;
    }

    private static bool IsWithoutRowId(SqlNode createStatement)
    {
        // Table options follow the column definitions group
        for (int i = 4; i < createStatement.Children.Count; i++)
        {
            if (createStatement.Is(i, "WITHOUT") && createStatement.Is(i + 1, "ROWID"))
                return true;
        }

        return false;
    }
}