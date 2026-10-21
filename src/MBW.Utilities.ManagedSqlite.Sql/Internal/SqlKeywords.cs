using System;
using System.Collections.Generic;
using System.Linq;

namespace MBW.Utilities.ManagedSqlite.Sql.Internal;

internal static class SqlKeywords
{
    public static HashSet<string> NonColumnKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // column-constraint
        "CONSTRAINT", "PRIMARY", "NOT", "UNIQUE",
        "CHECK", "DEFAULT", "COLLATE",

        // table-constraint
        "CONSTRAINT", "PRIMARY", "UNIQUE", "CHECK", "FOREIGN",

        // foreign-key-clause
        "REFERENCES"
    };

    /// <summary>
    /// Keywords that start a column-constraint. The declared type of a column is everything between its name and the first of these.
    /// </summary>
    public static HashSet<string> ColumnConstraintKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CONSTRAINT", "PRIMARY", "NOT", "NULL", "UNIQUE", "CHECK", "DEFAULT", "COLLATE", "REFERENCES", "GENERATED", "AS",

        // defer_subclause is a column constraint of its own in SQLite's grammar (ccons ::= defer_subclause in parse.y),
        // so "a INTEGER DEFERRABLE PRIMARY KEY" declares the type INTEGER. "NOT DEFERRABLE" starts with NOT, above.
        "DEFERRABLE"
    };

}