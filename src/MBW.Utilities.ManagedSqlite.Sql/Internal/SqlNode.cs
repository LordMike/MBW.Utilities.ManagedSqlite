using System;
using System.Collections.Generic;
using Superpower.Model;

namespace MBW.Utilities.ManagedSqlite.Sql.Internal;

internal class SqlNode
{
    public Token<SqlToken> Token { get; set; }

    public List<SqlNode> Children { get; set; }

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

    public bool Is(int index, SqlToken type)
    {
        return IsGroup() && Children.Count > index && Children[index].Is(type);
    }

    public bool Is(int index, string text)
    {
        return IsGroup() && Children.Count > index && Children[index].Is(text);
    }

    public bool IsGroup()
    {
        return Children != null;
    }

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

        return Children[index].Token.ToStringValue().Trim('"', '[', ']', '`', '\'');
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