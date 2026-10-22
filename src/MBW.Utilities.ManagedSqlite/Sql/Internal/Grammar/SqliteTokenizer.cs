using System;
using System.Collections.Generic;
using MBW.Utilities.ManagedSqlite.Core.Helpers;

namespace MBW.Utilities.ManagedSqlite.Sql.Internal.Grammar;

/// <summary>
/// SQLite's tokenizer, sqlite3GetToken in tokenize.c, ported line by line. SQLite reads UTF-8 bytes; here a character
/// above 0x7F stands for the bytes of its UTF-8 encoding, which SQLite's tables treat alike (CC_ID, an identifier
/// character), except a byte order mark (EF BB BF), which is whitespace where a token starts.
/// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/tokenize.c#L273 (sqlite3GetToken)
/// </summary>
internal static class SqliteTokenizer
{
    // Character classes, CC_* in tokenize.c
    private const byte CC_X = 0, CC_KYWD0 = 1, CC_KYWD = 2, CC_DIGIT = 3, CC_DOLLAR = 4, CC_VARALPHA = 5, CC_VARNUM = 6,
        CC_SPACE = 7, CC_QUOTE = 8, CC_QUOTE2 = 9, CC_PIPE = 10, CC_MINUS = 11, CC_LT = 12, CC_GT = 13, CC_EQ = 14,
        CC_BANG = 15, CC_SLASH = 16, CC_LP = 17, CC_RP = 18, CC_SEMI = 19, CC_PLUS = 20, CC_STAR = 21, CC_PERCENT = 22,
        CC_COMMA = 23, CC_AND = 24, CC_TILDA = 25, CC_DOT = 26, CC_ID = 27, CC_ILLEGAL = 28, CC_NUL = 29, CC_BOM = 30;

    /// <summary>
    /// aiClass in tokenize.c, for the ASCII range. Every other character is CC_ID, a byte order mark CC_BOM.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/tokenize.c#L61 (aiClass)
    /// </summary>
    private static readonly byte[] AsciiClass =
    {
        29, 28, 28, 28, 28, 28, 28, 28, 28, 7, 7, 28, 7, 7, 28, 28,
        28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28,
        7, 15, 8, 5, 4, 22, 24, 8, 17, 18, 21, 20, 23, 11, 26, 16,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 5, 19, 12, 14, 13, 6,
        5, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 0, 2, 2, 9, 28, 28, 28, 2,
        8, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 0, 2, 2, 28, 10, 28, 25, 28
    };

    private static readonly Dictionary<string, int> Keywords = BuildKeywords();

    private static Dictionary<string, int> BuildKeywords()
    {
        Dictionary<string, int> keywords = new Dictionary<string, int>(AsciiCaseInsensitiveComparer.Instance);
        foreach ((string keyword, int token) in SqliteGrammar.Keywords)
            keywords[keyword] = token;
        return keywords;
    }

    private static byte Class(string z, int i)
    {
        if (i >= z.Length)
            return CC_NUL;

        char c = z[i];
        if (c < 0x80)
            return AsciiClass[c];
        return c == '﻿' ? CC_BOM : CC_ID;
    }

    /// <summary>
    /// The character at <paramref name="i"/>, or 0 past the end, as SQLite reads the NUL that ends its string
    /// </summary>
    private static char At(string z, int i) => i < z.Length ? z[i] : '\0';

    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    private static bool IsXDigit(char c) => IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    /// <summary>
    /// sqlite3Isspace: space, tab, line feed, vertical tab, form feed, carriage return
    /// </summary>
    private static bool IsSpace(char c) => c == ' ' || (c >= '\t' && c <= '\r');

    /// <summary>
    /// IdChar in tokenize.c: a letter, digit, '_', '$', or any character outside ASCII (sqlite3CtypeMap &amp; 0x46)
    /// </summary>
    public static bool IsIdChar(char c) =>
        c > 0x7F || c == '_' || c == '$' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || IsDigit(c);

    /// <summary>
    /// The length of the token at <paramref name="pos"/> and its type, a TK_* code. At the end of the text this is a
    /// TK_ILLEGAL of length 0, as SQLite's CC_NUL case.
    /// </summary>
    public static int GetToken(string z, int pos, out int tokenType)
    {
        int i;
        char c;
        switch (Class(z, pos))
        {
            case CC_SPACE:
                for (i = 1; IsSpace(At(z, pos + i)); i++)
                {
                }

                tokenType = TK.SPACE;
                return i;
            case CC_MINUS:
                if (At(z, pos + 1) == '-')
                {
                    for (i = 2; (c = At(z, pos + i)) != 0 && c != '\n'; i++)
                    {
                    }

                    tokenType = TK.COMMENT;
                    return i;
                }

                if (At(z, pos + 1) == '>')
                {
                    tokenType = TK.PTR;
                    return 2 + (At(z, pos + 2) == '>' ? 1 : 0);
                }

                tokenType = TK.MINUS;
                return 1;
            case CC_LP:
                tokenType = TK.LP;
                return 1;
            case CC_RP:
                tokenType = TK.RP;
                return 1;
            case CC_SEMI:
                tokenType = TK.SEMI;
                return 1;
            case CC_PLUS:
                tokenType = TK.PLUS;
                return 1;
            case CC_STAR:
                tokenType = TK.STAR;
                return 1;
            case CC_SLASH:
                if (At(z, pos + 1) != '*' || At(z, pos + 2) == 0)
                {
                    tokenType = TK.SLASH;
                    return 1;
                }

                for (i = 3, c = At(z, pos + 2); (c != '*' || At(z, pos + i) != '/') && (c = At(z, pos + i)) != 0; i++)
                {
                }

                if (c != 0)
                    i++;
                tokenType = TK.COMMENT;
                return i;
            case CC_PERCENT:
                tokenType = TK.REM;
                return 1;
            case CC_EQ:
                tokenType = TK.EQ;
                return 1 + (At(z, pos + 1) == '=' ? 1 : 0);
            case CC_LT:
                c = At(z, pos + 1);
                if (c == '=')
                {
                    tokenType = TK.LE;
                    return 2;
                }

                if (c == '>')
                {
                    tokenType = TK.NE;
                    return 2;
                }

                if (c == '<')
                {
                    tokenType = TK.LSHIFT;
                    return 2;
                }

                tokenType = TK.LT;
                return 1;
            case CC_GT:
                c = At(z, pos + 1);
                if (c == '=')
                {
                    tokenType = TK.GE;
                    return 2;
                }

                if (c == '>')
                {
                    tokenType = TK.RSHIFT;
                    return 2;
                }

                tokenType = TK.GT;
                return 1;
            case CC_BANG:
                if (At(z, pos + 1) != '=')
                {
                    tokenType = TK.ILLEGAL;
                    return 1;
                }

                tokenType = TK.NE;
                return 2;
            case CC_PIPE:
                if (At(z, pos + 1) != '|')
                {
                    tokenType = TK.BITOR;
                    return 1;
                }

                tokenType = TK.CONCAT;
                return 2;
            case CC_COMMA:
                tokenType = TK.COMMA;
                return 1;
            case CC_AND:
                tokenType = TK.BITAND;
                return 1;
            case CC_TILDA:
                tokenType = TK.BITNOT;
                return 1;
            case CC_QUOTE:
            {
                char delim = z[pos];
                for (i = 1; (c = At(z, pos + i)) != 0; i++)
                {
                    if (c == delim)
                    {
                        if (At(z, pos + i + 1) == delim)
                            i++;
                        else
                            break;
                    }
                }

                if (c == '\'')
                {
                    tokenType = TK.STRING;
                    return i + 1;
                }

                if (c != 0)
                {
                    tokenType = TK.ID;
                    return i + 1;
                }

                tokenType = TK.ILLEGAL;
                return i;
            }
            case CC_DOT:
                if (!IsDigit(At(z, pos + 1)))
                {
                    tokenType = TK.DOT;
                    return 1;
                }

                // A digit follows, so this is a number that starts with "."
                return Number(z, pos, out tokenType);
            case CC_DIGIT:
                return Number(z, pos, out tokenType);
            case CC_QUOTE2:
                for (i = 1, c = z[pos]; c != ']' && (c = At(z, pos + i)) != 0; i++)
                {
                }

                tokenType = c == ']' ? TK.ID : TK.ILLEGAL;
                return i;
            case CC_VARNUM:
                tokenType = TK.VARIABLE;
                for (i = 1; IsDigit(At(z, pos + i)); i++)
                {
                }

                return i;
            case CC_DOLLAR:
            case CC_VARALPHA:
            {
                int n = 0;
                tokenType = TK.VARIABLE;
                for (i = 1; (c = At(z, pos + i)) != 0; i++)
                {
                    if (IsIdChar(c))
                    {
                        n++;
                    }
                    else if (c == '(' && n > 0)
                    {
                        do
                        {
                            i++;
                        } while ((c = At(z, pos + i)) != 0 && !IsSpace(c) && c != ')');

                        if (c == ')')
                            i++;
                        else
                            tokenType = TK.ILLEGAL;
                        break;
                    }
                    else if (c == ':' && At(z, pos + i + 1) == ':')
                    {
                        i++;
                    }
                    else
                    {
                        break;
                    }
                }

                if (n == 0)
                    tokenType = TK.ILLEGAL;
                return i;
            }
            case CC_KYWD0:
            {
                if (Class(z, pos + 1) > CC_KYWD)
                {
                    i = 1;
                    break;
                }

                for (i = 2; Class(z, pos + i) <= CC_KYWD; i++)
                {
                }

                if (IsIdChar(At(z, pos + i)))
                {
                    // Started with keyword characters, but goes on with one that is not, so this is an identifier
                    i++;
                    break;
                }

                tokenType = Keywords.TryGetValue(z.Substring(pos, i), out int keyword) ? keyword : TK.ID;
                return i;
            }
            case CC_X:
                if (At(z, pos + 1) == '\'')
                {
                    tokenType = TK.BLOB;
                    for (i = 2; IsXDigit(At(z, pos + i)); i++)
                    {
                    }

                    if (At(z, pos + i) != '\'' || i % 2 != 0)
                    {
                        tokenType = TK.ILLEGAL;
                        while (At(z, pos + i) != 0 && At(z, pos + i) != '\'')
                            i++;
                    }

                    if (At(z, pos + i) != 0)
                        i++;
                    return i;
                }

                // Not a BLOB literal, so an identifier: no keyword starts with x
                i = 1;
                break;
            case CC_KYWD:
            case CC_ID:
                i = 1;
                break;
            case CC_BOM:
                // A byte order mark is whitespace where a token starts. In UTF-8 it is the bytes EF BB BF.
                tokenType = TK.SPACE;
                return 1;
            case CC_NUL:
                tokenType = TK.ILLEGAL;
                return 0;
            default:
                tokenType = TK.ILLEGAL;
                return 1;
        }

        while (IsIdChar(At(z, pos + i)))
            i++;
        tokenType = TK.ID;
        return i;
    }

    /// <summary>
    /// The CC_DIGIT case: decimal, hexadecimal and real numbers, with '_' digit separators (TK_QNUMBER). A number
    /// directly followed by an identifier character is TK_ILLEGAL.
    /// </summary>
    private static int Number(string z, int pos, out int tokenType)
    {
        const char separator = '_';
        int i;
        tokenType = TK.INTEGER;
        if (z[pos] == '0' && (At(z, pos + 1) == 'x' || At(z, pos + 1) == 'X') && IsXDigit(At(z, pos + 2)))
        {
            for (i = 3; ; i++)
            {
                if (!IsXDigit(At(z, pos + i)))
                {
                    if (At(z, pos + i) == separator)
                        tokenType = TK.QNUMBER;
                    else
                        break;
                }
            }
        }
        else
        {
            for (i = 0; ; i++)
            {
                if (!IsDigit(At(z, pos + i)))
                {
                    if (At(z, pos + i) == separator)
                        tokenType = TK.QNUMBER;
                    else
                        break;
                }
            }

            if (At(z, pos + i) == '.')
            {
                if (tokenType == TK.INTEGER)
                    tokenType = TK.FLOAT;
                for (i++; ; i++)
                {
                    if (!IsDigit(At(z, pos + i)))
                    {
                        if (At(z, pos + i) == separator)
                            tokenType = TK.QNUMBER;
                        else
                            break;
                    }
                }
            }

            char e = At(z, pos + i);
            if ((e == 'e' || e == 'E') &&
                (IsDigit(At(z, pos + i + 1)) || ((At(z, pos + i + 1) == '+' || At(z, pos + i + 1) == '-') && IsDigit(At(z, pos + i + 2)))))
            {
                if (tokenType == TK.INTEGER)
                    tokenType = TK.FLOAT;
                for (i += 2; ; i++)
                {
                    if (!IsDigit(At(z, pos + i)))
                    {
                        if (At(z, pos + i) == separator)
                            tokenType = TK.QNUMBER;
                        else
                            break;
                    }
                }
            }
        }

        while (IsIdChar(At(z, pos + i)))
        {
            tokenType = TK.ILLEGAL;
            i++;
        }

        return i;
    }

    /// <summary>
    /// The type of the next token after <paramref name="pos"/> that is not whitespace or a comment, with every token
    /// that can be a name read as TK_ID: getToken in tokenize.c, used to tell whether WINDOW, OVER and FILTER are
    /// keywords
    /// </summary>
    public static int PeekNameToken(string z, ref int pos)
    {
        int t;
        do
        {
            pos += GetToken(z, pos, out t);
        } while (t == TK.SPACE || t == TK.COMMENT);

        if (t == TK.ID || t == TK.STRING || t == TK.JOIN_KW || t == TK.WINDOW || t == TK.OVER ||
            (t < SqliteGrammar.Fallback.Length && SqliteGrammar.Fallback[t] == TK.ID))
            t = TK.ID;
        return t;
    }

    /// <summary>
    /// sqlite3Dequote in util.c: removes the quotes from a "", '', `` or [] quoted text and undoubles the quote
    /// character inside. Text that does not start with a quote is returned as is.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/util.c#L298 (sqlite3Dequote)
    /// </summary>
    public static string Dequote(string z)
    {
        if (z.Length == 0)
            return z;

        char quote = z[0];
        if (quote != '"' && quote != '\'' && quote != '`' && quote != '[')
            return z;
        if (quote == '[')
            quote = ']';

        System.Text.StringBuilder sb = new System.Text.StringBuilder(z.Length);
        for (int i = 1; i < z.Length; i++)
        {
            if (z[i] == quote)
            {
                if (i + 1 < z.Length && z[i + 1] == quote)
                {
                    sb.Append(quote);
                    i++;
                }
                else
                {
                    break;
                }
            }
            else
            {
                sb.Append(z[i]);
            }
        }

        return sb.ToString();
    }

    public static bool IsQuote(char c) => c == '"' || c == '\'' || c == '`' || c == '[';
}
