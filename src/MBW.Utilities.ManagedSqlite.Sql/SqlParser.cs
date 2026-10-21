using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using MBW.Utilities.ManagedSqlite.Core.Helpers;
using MBW.Utilities.ManagedSqlite.Sql.Internal;
using Superpower.Model;

namespace MBW.Utilities.ManagedSqlite.Sql;

internal static class SqlParser
{
    private static bool TryParseTokenList(TokenList<SqlToken> list, [NotNullWhen(true)] out SqlNode? rootNode)
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
                // Outside parentheses a comma only separates the table options after the column list, as in
                // "(...) STRICT, WITHOUT ROWID". It carries no structure there, so it is dropped when it follows a
                // complete option; anywhere else at the top level it is an error, as in SQLite.
                if (stack.Count < 2)
                {
                    List<SqlNode>? top = stack.Peek().Children;
                    SqlNode? last = top?.Count > 0 ? top[top.Count - 1] : null;
                    if (last != null && (last.Is("ROWID") || last.Is("STRICT")))
                        continue;

                    return false;
                }

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

    public static bool TryParse(string? sql, [NotNullWhen(true)] out SqlTableDefinition? tableDefinition)
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

    private static bool TryParseInternal(string sql, [NotNullWhen(true)] out SqlTableDefinition? tableDefinition)
    {
        tableDefinition = null;

        // Parse out the SQL into series of column creation statements
        Result<TokenList<SqlToken>> tokens = SqlTokenizer.Tokenizer.TryTokenize(sql);

        if (!tokens.HasValue)
            return false;

        if (!TryParseTokenList(tokens.Value, out SqlNode? node))
            return false;

        // Check if this is a create table
        if (!node.Is(0, "CREATE") || !node.Is(1, "TABLE") || !node.Is(2, SqlToken.String) || !node.IsGroup(3))
            return false;

        SqlTableDefinition def = new SqlTableDefinition(node.GetString(2));

        // GetGroup only returns group nodes, which always have Children
        SqlNode columns = node.GetGroup(3);
        Debug.Assert(columns.IsGroup());
        int primaryKeyComponents = 0;
        SqlTableColumn? primaryKeyColumn = null;

        // The primary key terms in key order. A term that repeats an earlier one with the same collation is left out, as
        // SQLite does (isDupColumn in build.c), but "a, a COLLATE NOCASE" is two terms.
        List<(SqlTableColumn column, string? collation)> primaryKeyTerms = new List<(SqlTableColumn column, string? collation)>();

        // Columns that may alias the rowid if they turn out to be the sole primary key
        HashSet<SqlTableColumn> rowIdAliasCandidates = new HashSet<SqlTableColumn>();

        // VIRTUAL generated columns are not stored, so each one moves the later columns one position left in the record
        int virtualGeneratedColumns = 0;

        for (int columnIndex = 0; columnIndex < columns.Children!.Count; columnIndex++)
        {
            SqlNode columnSet = columns.Children[columnIndex];
            if (columnSet.Children == null || !columnSet.Children.Any() || !columnSet.Children.First().Is(SqlToken.String))
                continue;

            string firstToken = columnSet.Children.First().GetString();

            bool isConstraint = SqlKeywords.NonColumnKeywords.Contains(firstToken);
            string name;
            if (isConstraint)
            {
                // Table constraints need no comma between them (tconscomma ::= in parse.y), so one comma-separated set
                // can hold several, as in "UNIQUE (b) PRIMARY KEY (a)" or "CONSTRAINT name PRIMARY KEY (a)". Only one
                // can be the primary key.
                int pk = FindTablePrimaryKey(columnSet);
                if (pk >= 0)
                {
                    // GetGroup only returns group nodes, which always have Children
                    SqlNode pkColumns = columnSet.GetGroup(pk + 2);
                    Debug.Assert(pkColumns.IsGroup());

                    foreach (SqlNode pkColumn in pkColumns.Children!)
                    {
                        if (pkColumn.Is(SqlToken.String))
                            name = pkColumn.GetString();
                        else if (pkColumn.Is(0, SqlToken.String))
                            name = pkColumn.GetString(0);
                        else
                            return false;

                        if (!def.TryGetColumn(name, out SqlTableColumn? columnObject))
                            continue;

                        columnObject.IsPartOfPrimaryKey = true;

                        primaryKeyComponents++;
                        primaryKeyColumn = columnObject;
                        AddPrimaryKeyTerm(primaryKeyTerms, columnObject, GetCollation(pkColumn, 0) ?? columnObject.Collation);
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

            SqlTableColumn sqlTableColumn = new SqlTableColumn(columnIndex, name, GetDeclaredTypeName(columnSet), GetAffinityType(columnSet));
            sqlTableColumn.Collation = GetCollation(columnSet, 1);
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
                AddPrimaryKeyTerm(primaryKeyTerms, sqlTableColumn, sqlTableColumn.Collation);
            }

            // A column declared as exactly "INTEGER" may alias the rowid. As a quirk, SQLite does not
            // alias the rowid when the column-constraint is "PRIMARY KEY DESC" (a table-constraint
            // "PRIMARY KEY (x DESC)" does alias it). https://www.sqlite.org/lang_createtable.html#rowid
            if (IsDeclaredAsInteger(columnSet) && !IsColumnPrimaryKeyDesc(columnSet))
                rowIdAliasCandidates.Add(sqlTableColumn);
        }

        bool withoutRowId = IsWithoutRowId(node);
        def.IsWithoutRowId = withoutRowId;

        // Identify row-id substitutes
        if (primaryKeyComponents == 1 && primaryKeyColumn != null && rowIdAliasCandidates.Contains(primaryKeyColumn) && !withoutRowId)
        {
            def.ConfigureRowIdColumn(primaryKeyColumn);
        }

        // A WITHOUT ROWID table's record holds the primary key terms first, in key order, then the other stored columns
        // in declared order. A column that is in the key twice, with two collations, takes two positions, and is read
        // from the first. VIRTUAL generated columns are not stored, and can't be in the key.
        // convertToWithoutRowidTable in build.c: https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L2354
        if (withoutRowId)
        {
            HashSet<SqlTableColumn> keyColumns = new HashSet<SqlTableColumn>();
            int recordIndex = 0;
            foreach ((SqlTableColumn column, _) in primaryKeyTerms)
            {
                if (keyColumns.Add(column))
                    column.RecordIndex = recordIndex;
                recordIndex++;
            }

            foreach (SqlTableColumn column in def.Columns)
            {
                if (!keyColumns.Contains(column) && !column.IsVirtualGenerated)
                    column.RecordIndex = recordIndex++;
            }
        }

        tableDefinition = def;
        return true;
    }

    /// <summary>
    /// The declared type: everything between the column name and the first column constraint. GENERATED and ALWAYS
    /// are names SQLite's grammar also accepts in a type (%fallback ID in parse.y), so GENERATED starts a constraint
    /// only as "GENERATED ALWAYS AS". Like sqlite3AddColumn in build.c (https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L1490),
    /// a type text of 16 or more characters that ends in "always", or "generated always", loses those words.
    /// </summary>
    private static List<SqlNode> GetDeclaredType(SqlNode columnSet)
    {
        Debug.Assert(columnSet.IsGroup());

        List<SqlNode> type = new List<SqlNode>();
        for (int i = 1; i < columnSet.Children!.Count; i++)
        {
            SqlNode node = columnSet.Children[i];
            if (node.Is(SqlToken.String) && SqlKeywords.ColumnConstraintKeywords.Contains(node.GetString()) &&
                (!node.Is("GENERATED") || (columnSet.Is(i + 1, "ALWAYS") && columnSet.Is(i + 2, "AS"))))
                break;

            type.Add(node);
        }

        if (type.Count > 0 && type[type.Count - 1].Is("ALWAYS") && type[0].Token.HasValue)
        {
            TextSpan last = type[type.Count - 1].Token.Span;
            if (last.Position.Absolute + last.Length - type[0].Token.Span.Position.Absolute >= 16)
            {
                type.RemoveAt(type.Count - 1);
                if (type.Count > 0 && type[type.Count - 1].Is("GENERATED"))
                    type.RemoveAt(type.Count - 1);
            }
        }

        return type;
    }

    /// <summary>
    /// The declared type's words, each unquoted, joined by one space, without a size such as "(10, 5)". For a type
    /// written the usual way this is the type SQLite reports (PRAGMA table_info) without its size; SQLite itself keeps
    /// the original spacing and dequotes the type text as a whole. sqlite3AddColumn in build.c:
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L1490
    /// </summary>
    private static string GetDeclaredTypeName(SqlNode columnSet)
    {
        return string.Join(" ", GetDeclaredType(columnSet).Where(s => s.Is(SqlToken.String)).Select(s => SqlNode.Unquote(s.GetString())));
    }

    /// <summary>
    /// The collation named by the last "COLLATE name" in <paramref name="node"/>, from position <paramref name="start"/>,
    /// or null for none
    /// </summary>
    private static string? GetCollation(SqlNode node, int start)
    {
        if (!node.IsGroup())
            return null;

        // A later COLLATE replaces an earlier one, as in sqlite3AddCollateType in build.c
        string? collation = null;
        for (int i = start; i < node.Children.Count - 1; i++)
        {
            if (node.Is(i, "COLLATE") && node.Is(i + 1, SqlToken.String))
                collation = node.GetString(i + 1);
        }

        return collation;
    }

    /// <summary>
    /// The position of "PRIMARY KEY (...)" among the table constraints in <paramref name="constraints"/>, or -1
    /// </summary>
    private static int FindTablePrimaryKey(SqlNode constraints)
    {
        Debug.Assert(constraints.IsGroup());

        for (int i = 0; i < constraints.Children!.Count; i++)
        {
            if (constraints.Is(i, "PRIMARY") && constraints.Is(i + 1, "KEY") && constraints.IsGroup(i + 2))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Adds a primary key term, unless an earlier term has the same column and collation. A term without COLLATE has
    /// its column's collation, and a column without one has BINARY; collation names compare ignoring ASCII case.
    /// isDupColumn in build.c: https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L2274
    /// </summary>
    private static void AddPrimaryKeyTerm(List<(SqlTableColumn column, string? collation)> terms, SqlTableColumn column, string? collation)
    {
        string effective = collation ?? "BINARY";

        foreach ((SqlTableColumn existing, string? existingCollation) in terms)
        {
            if (existing == column && AsciiCaseInsensitiveComparer.Instance.Equals(existingCollation ?? "BINARY", effective))
                return;
        }

        terms.Add((column, collation));
    }

    /// <summary>
    /// The CLR type of the column's affinity, which SQLite determines from substrings of the declared type, by the first
    /// rule that matches. https://www.sqlite.org/datatype3.html#determination_of_column_affinity
    /// </summary>
    private static Type GetAffinityType(SqlNode columnSet)
    {
        string declaredType = GetDeclaredTypeName(columnSet);

        bool Contains(string value) => declaredType.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

        if (Contains("INT"))
            return typeof(long);
        if (Contains("CHAR") || Contains("CLOB") || Contains("TEXT"))
            return typeof(string);
        if (Contains("BLOB") || declaredType.Length == 0)
            return typeof(byte[]);
        if (Contains("REAL") || Contains("FLOA") || Contains("DOUB"))
            return typeof(double);

        // NUMERIC affinity, reported as long although SQLite may store a real, text or blob there
        return typeof(long);
    }

    private static bool IsDeclaredAsInteger(SqlNode columnSet)
    {
        List<SqlNode> declaredType = GetDeclaredType(columnSet);

        // Exactly INTEGER once unquoted, with no size, so 'INTEGER' and [INTEGER] alias the rowid too (sqlite3AddColumn
        // sets the column's eCType from the dequoted type text, in build.c)
        return declaredType.Count == 1 &&
               declaredType[0].Is(SqlToken.String) &&
               AsciiCaseInsensitiveComparer.Instance.Equals(SqlNode.Unquote(declaredType[0].GetString()), "INTEGER");
    }

    /// <summary>
    /// A generated column is "[GENERATED ALWAYS] AS (expr) [VIRTUAL | STORED]", and is VIRTUAL unless STORED is given.
    /// https://www.sqlite.org/gencol.html
    /// </summary>
    private static bool IsVirtualGenerated(SqlNode columnSet)
    {
        Debug.Assert(columnSet.IsGroup());

        for (int i = 1; i < columnSet.Children!.Count; i++)
        {
            if (columnSet.Is(i, "AS") && columnSet.IsGroup(i + 1))
                return !columnSet.Is(i + 2, "STORED");
        }

        return false;
    }

    private static bool IsColumnPrimaryKeyDesc(SqlNode columnSet)
    {
        Debug.Assert(columnSet.IsGroup());

        for (int i = 1; i < columnSet.Children!.Count; i++)
        {
            if (columnSet.Is(i, "PRIMARY") && columnSet.Is(i + 1, "KEY"))
                return columnSet.Is(i + 2, "DESC");
        }

        return false;
    }

    private static bool IsWithoutRowId(SqlNode createStatement)
    {
        // Table options follow the column definitions group
        Debug.Assert(createStatement.IsGroup());

        for (int i = 4; i < createStatement.Children!.Count; i++)
        {
            if (createStatement.Is(i, "WITHOUT") && createStatement.Is(i + 1, "ROWID"))
                return true;
        }

        return false;
    }
}