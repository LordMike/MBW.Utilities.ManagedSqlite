using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Superpower.Model;

namespace MBW.Utilities.ManagedSqlite.Sql.Internal;

internal class SqlNode
{
    public Token<SqlToken> Token { get; set; }

    /// <summary>
    /// The child nodes of a group, null for a token node
    /// </summary>
    public List<SqlNode>? Children { get; set; }

    public SqlNode()
    {
    }

    public SqlNode(Token<SqlToken> token)
    {
        Token = token;
    }

    public void AddChild(SqlNode node)
    {
        if (Token.HasValue)
            throw new Exception();

        if (Children == null)
            Children = new List<SqlNode>();
        Children.Add(node);
    }

    public bool Is(SqlToken type)
    {
        return !IsGroup() && Token.Kind == type;
    }

    public bool Is(string text)
    {
        return !IsGroup() && Token.Kind == SqlToken.String && Token.Span.EqualsValueIgnoreCase(text);
    }

    [MemberNotNullWhen(true, nameof(Children))]
    public bool Is(int index, SqlToken type)
    {
        return IsGroup() && Children.Count > index && Children[index].Is(type);
    }

    [MemberNotNullWhen(true, nameof(Children))]
    public bool Is(int index, string text)
    {
        return IsGroup() && Children.Count > index && Children[index].Is(text);
    }

    [MemberNotNullWhen(true, nameof(Children))]
    public bool IsGroup()
    {
        return Children != null;
    }

    [MemberNotNullWhen(true, nameof(Children))]
    public bool IsGroup(int index)
    {
        return IsGroup() && Children.Count > index && Children[index].IsGroup();
    }

    public string GetString()
    {
        if (!Is(SqlToken.String))
            throw new Exception();

        return Token.ToStringValue();
    }

    public string GetString(int index)
    {
        if (!Is(index, SqlToken.String))
            throw new Exception();

        return Unquote(Children[index].Token.ToStringValue());
    }

    /// <summary>
    /// Removes identifier quoting ("x", 'x', `x` or [x]) and unescapes doubled quote characters, e.g. "col""two" is col"two
    /// </summary>
    private static string Unquote(string value)
    {
        if (value.Length < 2)
            return value;

        char first = value[0];
        char last = value[value.Length - 1];

        if (first == '[' && last == ']')
            return value.Substring(1, value.Length - 2);

        if ((first == '"' || first == '\'' || first == '`') && last == first)
            return value.Substring(1, value.Length - 2).Replace(new string(first, 2), first.ToString());

        return value;
    }

    public SqlNode GetGroup(int index)
    {
        if (!IsGroup(index))
            throw new Exception();

        return Children[index];
    }

    public override string ToString()
    {
        if (Token.HasValue)
            return $"Token {Token.Kind} ({Token.ToStringValue()})";

        return $"Group {Children.Count} childs";
    }
}