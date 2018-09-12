using System;
using System.Collections.Generic;
using System.Linq;
using Superpower;
using Superpower.Model;
using Superpower.Parsers;

namespace MBW.Utilities.ManagedSqlite.Sql.Internal
{
    internal static class SqlKeywords
    {
        public static TokenListParser<SqlToken, Token<SqlToken>> Abort { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "ABORT");
        public static TokenListParser<SqlToken, Token<SqlToken>> Action { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "ACTION");
        public static TokenListParser<SqlToken, Token<SqlToken>> Add { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "ADD");
        public static TokenListParser<SqlToken, Token<SqlToken>> After { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "AFTER");
        public static TokenListParser<SqlToken, Token<SqlToken>> All { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "ALL");
        public static TokenListParser<SqlToken, Token<SqlToken>> Alter { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "ALTER");
        public static TokenListParser<SqlToken, Token<SqlToken>> Analyze { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "ANALYZE");
        public static TokenListParser<SqlToken, Token<SqlToken>> And { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "AND");
        public static TokenListParser<SqlToken, Token<SqlToken>> As { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "AS");
        public static TokenListParser<SqlToken, Token<SqlToken>> Asc { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "ASC");
        public static TokenListParser<SqlToken, Token<SqlToken>> Attach { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "ATTACH");
        public static TokenListParser<SqlToken, Token<SqlToken>> Autoincrement { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "AUTOINCREMENT");
        public static TokenListParser<SqlToken, Token<SqlToken>> Before { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "BEFORE");
        public static TokenListParser<SqlToken, Token<SqlToken>> Begin { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "BEGIN");
        public static TokenListParser<SqlToken, Token<SqlToken>> Between { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "BETWEEN");
        public static TokenListParser<SqlToken, Token<SqlToken>> By { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "BY");
        public static TokenListParser<SqlToken, Token<SqlToken>> Cascade { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "CASCADE");
        public static TokenListParser<SqlToken, Token<SqlToken>> Case { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "CASE");
        public static TokenListParser<SqlToken, Token<SqlToken>> Cast { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "CAST");
        public static TokenListParser<SqlToken, Token<SqlToken>> Check { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "CHECK");
        public static TokenListParser<SqlToken, Token<SqlToken>> Collate { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "COLLATE");
        public static TokenListParser<SqlToken, Token<SqlToken>> Column { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "COLUMN");
        public static TokenListParser<SqlToken, Token<SqlToken>> Commit { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "COMMIT");
        public static TokenListParser<SqlToken, Token<SqlToken>> Conflict { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "CONFLICT");
        public static TokenListParser<SqlToken, Token<SqlToken>> Constraint { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "CONSTRAINT");
        public static TokenListParser<SqlToken, Token<SqlToken>> Create { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "CREATE");
        public static TokenListParser<SqlToken, Token<SqlToken>> Cross { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "CROSS");
        public static TokenListParser<SqlToken, Token<SqlToken>> Current_Date { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "CURRENT_DATE");
        public static TokenListParser<SqlToken, Token<SqlToken>> Current_Time { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "CURRENT_TIME");
        public static TokenListParser<SqlToken, Token<SqlToken>> Current_Timestamp { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "CURRENT_TIMESTAMP");
        public static TokenListParser<SqlToken, Token<SqlToken>> Database { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "DATABASE");
        public static TokenListParser<SqlToken, Token<SqlToken>> Default { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "DEFAULT");
        public static TokenListParser<SqlToken, Token<SqlToken>> Deferrable { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "DEFERRABLE");
        public static TokenListParser<SqlToken, Token<SqlToken>> Deferred { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "DEFERRED");
        public static TokenListParser<SqlToken, Token<SqlToken>> Delete { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "DELETE");
        public static TokenListParser<SqlToken, Token<SqlToken>> Desc { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "DESC");
        public static TokenListParser<SqlToken, Token<SqlToken>> Detach { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "DETACH");
        public static TokenListParser<SqlToken, Token<SqlToken>> Distinct { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "DISTINCT");
        public static TokenListParser<SqlToken, Token<SqlToken>> Drop { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "DROP");
        public static TokenListParser<SqlToken, Token<SqlToken>> Each { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "EACH");
        public static TokenListParser<SqlToken, Token<SqlToken>> Else { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "ELSE");
        public static TokenListParser<SqlToken, Token<SqlToken>> End { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "END");
        public static TokenListParser<SqlToken, Token<SqlToken>> Escape { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "ESCAPE");
        public static TokenListParser<SqlToken, Token<SqlToken>> Except { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "EXCEPT");
        public static TokenListParser<SqlToken, Token<SqlToken>> Exclusive { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "EXCLUSIVE");
        public static TokenListParser<SqlToken, Token<SqlToken>> Exists { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "EXISTS");
        public static TokenListParser<SqlToken, Token<SqlToken>> Explain { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "EXPLAIN");
        public static TokenListParser<SqlToken, Token<SqlToken>> Fail { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "FAIL");
        public static TokenListParser<SqlToken, Token<SqlToken>> For { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "FOR");
        public static TokenListParser<SqlToken, Token<SqlToken>> Foreign { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "FOREIGN");
        public static TokenListParser<SqlToken, Token<SqlToken>> From { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "FROM");
        public static TokenListParser<SqlToken, Token<SqlToken>> Full { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "FULL");
        public static TokenListParser<SqlToken, Token<SqlToken>> Glob { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "GLOB");
        public static TokenListParser<SqlToken, Token<SqlToken>> Group { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "GROUP");
        public static TokenListParser<SqlToken, Token<SqlToken>> Having { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "HAVING");
        public static TokenListParser<SqlToken, Token<SqlToken>> If { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "IF");
        public static TokenListParser<SqlToken, Token<SqlToken>> Ignore { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "IGNORE");
        public static TokenListParser<SqlToken, Token<SqlToken>> Immediate { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "IMMEDIATE");
        public static TokenListParser<SqlToken, Token<SqlToken>> In { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "IN");
        public static TokenListParser<SqlToken, Token<SqlToken>> Index { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "INDEX");
        public static TokenListParser<SqlToken, Token<SqlToken>> Indexed { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "INDEXED");
        public static TokenListParser<SqlToken, Token<SqlToken>> Initially { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "INITIALLY");
        public static TokenListParser<SqlToken, Token<SqlToken>> Inner { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "INNER");
        public static TokenListParser<SqlToken, Token<SqlToken>> Insert { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "INSERT");
        public static TokenListParser<SqlToken, Token<SqlToken>> Instead { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "INSTEAD");
        public static TokenListParser<SqlToken, Token<SqlToken>> Intersect { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "INTERSECT");
        public static TokenListParser<SqlToken, Token<SqlToken>> Into { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "INTO");
        public static TokenListParser<SqlToken, Token<SqlToken>> Is { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "IS");
        public static TokenListParser<SqlToken, Token<SqlToken>> Isnull { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "ISNULL");
        public static TokenListParser<SqlToken, Token<SqlToken>> Join { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "JOIN");
        public static TokenListParser<SqlToken, Token<SqlToken>> Key { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "KEY");
        public static TokenListParser<SqlToken, Token<SqlToken>> Left { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "LEFT");
        public static TokenListParser<SqlToken, Token<SqlToken>> Like { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "LIKE");
        public static TokenListParser<SqlToken, Token<SqlToken>> Limit { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "LIMIT");
        public static TokenListParser<SqlToken, Token<SqlToken>> Match { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "MATCH");
        public static TokenListParser<SqlToken, Token<SqlToken>> Natural { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "NATURAL");
        public static TokenListParser<SqlToken, Token<SqlToken>> No { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "NO");
        public static TokenListParser<SqlToken, Token<SqlToken>> Not { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "NOT");
        public static TokenListParser<SqlToken, Token<SqlToken>> Notnull { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "NOTNULL");
        public static TokenListParser<SqlToken, Token<SqlToken>> Null { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "NULL");
        public static TokenListParser<SqlToken, Token<SqlToken>> Of { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "OF");
        public static TokenListParser<SqlToken, Token<SqlToken>> Offset { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "OFFSET");
        public static TokenListParser<SqlToken, Token<SqlToken>> On { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "ON");
        public static TokenListParser<SqlToken, Token<SqlToken>> Or { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "OR");
        public static TokenListParser<SqlToken, Token<SqlToken>> Order { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "ORDER");
        public static TokenListParser<SqlToken, Token<SqlToken>> Outer { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "OUTER");
        public static TokenListParser<SqlToken, Token<SqlToken>> Plan { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "PLAN");
        public static TokenListParser<SqlToken, Token<SqlToken>> Pragma { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "PRAGMA");
        public static TokenListParser<SqlToken, Token<SqlToken>> Primary { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "PRIMARY");
        public static TokenListParser<SqlToken, Token<SqlToken>> Query { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "QUERY");
        public static TokenListParser<SqlToken, Token<SqlToken>> Raise { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "RAISE");
        public static TokenListParser<SqlToken, Token<SqlToken>> Recursive { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "RECURSIVE");
        public static TokenListParser<SqlToken, Token<SqlToken>> References { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "REFERENCES");
        public static TokenListParser<SqlToken, Token<SqlToken>> Regexp { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "REGEXP");
        public static TokenListParser<SqlToken, Token<SqlToken>> Reindex { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "REINDEX");
        public static TokenListParser<SqlToken, Token<SqlToken>> Release { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "RELEASE");
        public static TokenListParser<SqlToken, Token<SqlToken>> Rename { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "RENAME");
        public static TokenListParser<SqlToken, Token<SqlToken>> Replace { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "REPLACE");
        public static TokenListParser<SqlToken, Token<SqlToken>> Restrict { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "RESTRICT");
        public static TokenListParser<SqlToken, Token<SqlToken>> Right { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "RIGHT");
        public static TokenListParser<SqlToken, Token<SqlToken>> Rollback { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "ROLLBACK");
        public static TokenListParser<SqlToken, Token<SqlToken>> Row { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "ROW");
        public static TokenListParser<SqlToken, Token<SqlToken>> Savepoint { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "SAVEPOINT");
        public static TokenListParser<SqlToken, Token<SqlToken>> Select { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "SELECT");
        public static TokenListParser<SqlToken, Token<SqlToken>> Set { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "SET");
        public static TokenListParser<SqlToken, Token<SqlToken>> Table { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "TABLE");
        public static TokenListParser<SqlToken, Token<SqlToken>> Temporary { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "TEMP").Or(Token.EqualToValueIgnoreCase(SqlToken.String, "TEMPORARY"));
        public static TokenListParser<SqlToken, Token<SqlToken>> Then { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "THEN");
        public static TokenListParser<SqlToken, Token<SqlToken>> To { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "TO");
        public static TokenListParser<SqlToken, Token<SqlToken>> Transaction { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "TRANSACTION");
        public static TokenListParser<SqlToken, Token<SqlToken>> Trigger { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "TRIGGER");
        public static TokenListParser<SqlToken, Token<SqlToken>> Union { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "UNION");
        public static TokenListParser<SqlToken, Token<SqlToken>> Unique { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "UNIQUE");
        public static TokenListParser<SqlToken, Token<SqlToken>> Update { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "UPDATE");
        public static TokenListParser<SqlToken, Token<SqlToken>> Using { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "USING");
        public static TokenListParser<SqlToken, Token<SqlToken>> Vacuum { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "VACUUM");
        public static TokenListParser<SqlToken, Token<SqlToken>> Values { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "VALUES");
        public static TokenListParser<SqlToken, Token<SqlToken>> View { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "VIEW");
        public static TokenListParser<SqlToken, Token<SqlToken>> Virtual { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "VIRTUAL");
        public static TokenListParser<SqlToken, Token<SqlToken>> When { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "WHEN");
        public static TokenListParser<SqlToken, Token<SqlToken>> Where { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "WHERE");
        public static TokenListParser<SqlToken, Token<SqlToken>> With { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "WITH");
        public static TokenListParser<SqlToken, Token<SqlToken>> Without { get; } = Token.EqualToValueIgnoreCase(SqlToken.String, "WITHOUT");

        public static TokenListParser<SqlToken, Token<SqlToken>> IfNotExists { get; } = If.Then(_ => Not).Then(_ => Exists);

        public static HashSet<string> ColumnKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // column-constraint
            "CONSTRAINT", "PRIMARY", "NOT", "UNIQUE",
            "CHECK", "DEFAULT", "COLLATE",

            // foreign-key-clause
            "REFERENCES"
        };

        public static (string[] words, Type type)[] TypeKeywords =
            new[] {
                // integer, numeric
                (new[] { "INT", "INTEGER", "TINYINT", "SMALLINT", "MEDIUMINT", "BIGINT", "INT2", "INT8", "UNSIGNED BIG INT", "NUMERIC", "DECIMAL", "BOOLEAN", "DATE", "DATETIME" }, typeof(long)),

                // text
                (new[] { "CHARACTER", "VARCHAR", "VARYING CHARACTER","NCHAR", "NATIVE CHARACTER", "NVARCHAR", "TEXT", "CLOB" }, typeof(string)),

                // blob
                (new[] { "BLOB" }, typeof(byte[])),

                // real
                (new[] { "REAL", "DOUBLE", "DOUBLE PRECISION", "FLOAT" }, typeof(double)),
            }.SelectMany(s => s.Item1.Select(x => (x.Split(' '), s.Item2))).ToArray();
    }
}