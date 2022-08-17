namespace MBW.Utilities.ManagedSqlite.Sql.Internal;

internal enum SqlToken
{
    None,
    String,
    Plus,
    Comma,
    ParenthesisStart,
    ParenthesisEnd,
    Semicolon,
    Dot
}