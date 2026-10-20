using System.Linq;
using Superpower;
using Superpower.Model;
using Superpower.Parsers;
using Superpower.Tokenizers;

namespace MBW.Utilities.ManagedSqlite.Sql.Internal;

internal static class SqlTokenizer
{
    private static TextParser<string> NonquotedIdentifier { get; } =
        from open in Character.Letter.Or(Character.EqualTo('_'))
        from chars in Character.LetterOrDigit.Or(Character.In('_', '$')).Many()
        select open + new string(chars);

    /// <summary>
    /// A quoted identifier or string literal. The quote character is escaped by doubling it, e.g. "col""two" is col"two.
    /// This is parsed as adjacent quoted segments ("col" and "two"), joined by the quote character.
    /// </summary>
    private static TextParser<string> Quoted(char quote) =>
        from segments in (
                from open in Character.EqualTo(quote)
                from chars in Character.Except(quote).Many()
                from close in Character.EqualTo(quote)
                select new string(chars))
            .AtLeastOnce()
        select string.Join(quote.ToString(), segments);

    private static TextParser<string> QuotedIdentifier { get; } =
        Quoted('"').Or(Quoted('`')).Or(Quoted('\''));

    private static TextParser<string> BracketedIdentifier { get; } =
        from open in Character.EqualTo('[')
        from chars in Character.Except(']').Many()
        from close in Character.EqualTo(']')
        select new string(chars);

    // Every token must consume at least one character, Superpower throws on zero-width tokens
    private static TextParser<char[]> Digits { get; } = Character.Digit.AtLeastOnce();

    private static TextParser<TextSpan> HexLiteral { get; } =
        Span.MatchedBy(Span.EqualToIgnoreCase("0x").IgnoreThen(Character.HexDigit.AtLeastOnce()));

    // 1, 1.5, 1., .5, 1e6, 1.5E-3
    // Only the matched span is used, never the parsed values, so OptionalOrDefault<char[]> is typed to match the other branches
    private static TextParser<TextSpan> NumericLiteral { get; } =
        Span.MatchedBy(
            Digits.IgnoreThen(Character.EqualTo('.').IgnoreThen(Character.Digit.Many()).OptionalOrDefault<char[]>())
                .Or(Character.EqualTo('.').IgnoreThen(Digits).Try())
                .IgnoreThen(Character.In('e', 'E')
                    .IgnoreThen(Character.In('+', '-').Optional())
                    .IgnoreThen(Digits)
                    .Try()
                    .OptionalOrDefault<char[]>()));

    // Multi-character operators are tried before their single-character prefixes
    private static TextParser<TextSpan> Operator { get; } =
        new[] { "->>", "->", "||", "<<", ">>", "<=", ">=", "==", "!=", "<>" }
            .Select(op => Span.EqualTo(op).Try())
            .Aggregate((a, b) => a.Or(b))
            .Or(Span.MatchedBy(Character.In('=', '<', '>', '!', '*', '/', '%', '-', '|', '&', '~')));

    public static Tokenizer<SqlToken> Tokenizer { get; } = new TokenizerBuilder<SqlToken>()
        .Ignore(Span.WhiteSpace)
        .Ignore(Comment.SqlStyle)
        .Ignore(Comment.CStyle)
        .Match(HexLiteral, SqlToken.String)
        .Match(NumericLiteral, SqlToken.String)
        .Match(Character.EqualTo('.'), SqlToken.Dot)
        .Match(Character.EqualTo('+'), SqlToken.Plus)
        .Match(Character.EqualTo(','), SqlToken.Comma)
        .Match(Character.EqualTo('('), SqlToken.ParenthesisStart)
        .Match(Character.EqualTo(')'), SqlToken.ParenthesisEnd)
        .Match(Character.EqualTo(';'), SqlToken.Semicolon)
        .Match(Operator, SqlToken.Operator)
        .Match(QuotedIdentifier, SqlToken.String)
        .Match(BracketedIdentifier, SqlToken.String)
        .Match(NonquotedIdentifier, SqlToken.String)
        .Build();
}
