using Superpower;
using Superpower.Parsers;
using Superpower.Tokenizers;

namespace MBW.Utilities.ManagedSqlite.Sql.Internal
{
    internal static class SqlTokenizer
    {
        private static TextParser<string> NonquotedIdentifier { get; } =
            from open in Character.Letter.Or(Character.EqualTo('_'))
            from chars in Character.LetterOrDigit.Or(Character.EqualTo('_')).Many()
            select open + new string(chars);

        private static TextParser<string> QuotedIdentifier { get; } =
            from open in Character.In('"', '[', '`', '\'')
            from chars in Character.Except(open).Many()
            from close in Character.EqualTo(open)
            select new string(chars);

        private static TextParser<string> NumericLiteral { get; } =
            from digits in Character.Digit.Many()
            from @decimal in Character.EqualTo('.').Optional()
            from decimals in Character.Digit.Many()
            select new string(digits) + @decimal + new string(decimals);

        public static Tokenizer<SqlToken> Tokenizer { get; } = new TokenizerBuilder<SqlToken>()
            .Ignore(Span.WhiteSpace)
            .Match(Character.EqualTo('.'), SqlToken.Dot)
            .Match(Character.EqualTo('+'), SqlToken.Plus)
            .Match(Character.EqualTo(','), SqlToken.Comma)
            .Match(Character.EqualTo('('), SqlToken.ParenthesisStart)
            .Match(Character.EqualTo(')'), SqlToken.ParenthesisEnd)
            .Match(Character.EqualTo(';'), SqlToken.Semicolon)
            .Match(QuotedIdentifier, SqlToken.String)
            .Match(NonquotedIdentifier, SqlToken.String)
            .Match(NumericLiteral, SqlToken.String)
            .Build();
    }
}