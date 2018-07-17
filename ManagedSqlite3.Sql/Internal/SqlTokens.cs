using System.Collections.Generic;
using System.Linq;
using ManagedSqlite3.Sql;
using ManagedSqlite3.Sql.Internal;
using Superpower;
using Superpower.Model;
using Superpower.Parsers;

namespace ConsoleApp14
{
    internal static class SqlTokens
    {
        private static TokenListParser<SqlToken, Token<SqlToken>[]> StringsOrParenthesisedStrings { get; } =
            Token.EqualTo(SqlToken.String).AtLeastOnce()
                .Or(from open in Token.EqualTo(SqlToken.ParenthesisStart)
                    from items in Parse.Ref(() => StringsOrParenthesisedStrings)
                    from close in Token.EqualTo(SqlToken.ParenthesisEnd)
                    select items);

        private static TokenListParser<SqlToken, List<string>> StringsOrParenthesisedStringsLists { get; } =
            StringsOrParenthesisedStrings.Select(s => s.Select(x => x.ToStringValue()).ToList());

        private static TokenListParser<SqlToken, TableName> TableName { get; } =
            (from schema in Token.EqualTo(SqlToken.String)
                from a1 in Token.EqualTo(SqlToken.Dot)
                from name in Token.EqualTo(SqlToken.String)
                select new TableName
                {
                    Name = name.Span.ToStringValue(),
                    Schema = schema.Span.ToStringValue()
                }).Try().Or(
                from name in Token.EqualTo(SqlToken.String)
                select new TableName
                {
                    Name = name.Span.ToStringValue()
                });

        private static TokenListParser<SqlToken, Token<SqlToken>?> ConflictClause { get; } =
            (from a1 in SqlKeywords.On
                from a2 in SqlKeywords.Conflict
                from a3 in SqlKeywords.Rollback.Or(SqlKeywords.Abort).Or(SqlKeywords.Fail).Or(SqlKeywords.Ignore).Or(SqlKeywords.Replace)
                select a3).Optional();

        private static TokenListParser<SqlToken, List<string>[]> PossibleColumnDef { get; } =
            StringsOrParenthesisedStringsLists.AtLeastOnce();

        public static TokenListParser<SqlToken, SqlCreateTableSchema> CreateTable { get; } =
            from a1 in SqlKeywords.Create
            from temporary in SqlKeywords.Temporary.Optional()
            from a3 in SqlKeywords.Table
            from a4 in SqlKeywords.IfNotExists.Optional()
            from name in TableName
            from a5 in Token.EqualTo(SqlToken.ParenthesisStart)
            from columns in PossibleColumnDef.AtLeastOnceDelimitedBy(Token.EqualTo(SqlToken.Comma))
            from a6 in Token.EqualTo(SqlToken.ParenthesisEnd)
            select new SqlCreateTableSchema
            {
                TableName = name,
                Columns = columns
            };
    }
}