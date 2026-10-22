using System.Collections.Generic;

namespace MBW.Utilities.ManagedSqlite.Sql.Internal.Grammar;

/// <summary>
/// The table a CREATE TABLE statement builds, as far as reading it needs: SQLite's Table, Column and Index
/// (sqliteInt.h), filled in by the same steps as build.c
/// </summary>
internal sealed class ParsedTable
{
    // TF_* in sqliteInt.h, for the flags kept here
    public const int TF_HasPrimaryKey = 0x0004, TF_Autoincrement = 0x0008, TF_HasVirtual = 0x0020,
        TF_HasStored = 0x0040, TF_HasGenerated = 0x0060, TF_WithoutRowid = 0x0080, TF_Strict = 0x00010000;

    public readonly string Name;
    public readonly List<ParsedColumn> Columns = new List<ParsedColumn>();

    /// <summary>
    /// The implied indexes of the PRIMARY KEY and UNIQUE constraints, in the order they were made (pIndex)
    /// </summary>
    public readonly List<ParsedIndex> Indexes = new List<ParsedIndex>();

    /// <summary>
    /// For a WITHOUT ROWID table, the columns its records hold, in order: the PRIMARY KEY index's columns
    /// (convertToWithoutRowidTable in build.c)
    /// </summary>
    public readonly List<int> RecordColumns = new List<int>();

    public int Flags;

    /// <summary>
    /// iPKey: the INTEGER PRIMARY KEY column, which is the rowid, or -1
    /// </summary>
    public int PrimaryKeyColumn = -1;

    /// <summary>
    /// keyConf: the ON CONFLICT of the INTEGER PRIMARY KEY
    /// </summary>
    public int KeyConflict;

    /// <summary>
    /// iPkSortOrder: the sort order of the INTEGER PRIMARY KEY's term, for a WITHOUT ROWID table
    /// </summary>
    public int PrimaryKeySortOrder;

    /// <summary>
    /// The number of columns that are not VIRTUAL (nNVCol)
    /// </summary>
    public int StoredColumnCount;

    public ParsedTable(string name)
    {
        Name = name;
    }

    public bool Has(int flags) => (Flags & flags) != 0;

    /// <summary>
    /// sqlite3PrimaryKeyIndex in build.c: the index that is the PRIMARY KEY, or null
    /// </summary>
    public ParsedIndex? PrimaryKeyIndex
    {
        get
        {
            foreach (ParsedIndex index in Indexes)
            {
                if (index.IsPrimaryKey)
                    return index;
            }

            return null;
        }
    }

    /// <summary>
    /// sqlite3ColumnIndex in select.c: the column with this name, ignoring ASCII case, or -1
    /// </summary>
    public int ColumnIndex(string name)
    {
        for (int i = 0; i < Columns.Count; i++)
        {
            if (MBW.Utilities.ManagedSqlite.Core.Helpers.AsciiCaseInsensitiveComparer.Instance.Equals(Columns[i].Name, name))
                return i;
        }

        return -1;
    }
}

internal sealed class ParsedColumn
{
    // COLFLAG_* in sqliteInt.h, for the flags kept here
    public const int COLFLAG_PRIMKEY = 0x0001, COLFLAG_UNIQUE = 0x0008, COLFLAG_HASTYPE = 0x0004,
        COLFLAG_VIRTUAL = 0x0020, COLFLAG_STORED = 0x0040, COLFLAG_GENERATED = 0x0060;

    // COLTYPE_* in sqliteInt.h
    public const int COLTYPE_CUSTOM = 0, COLTYPE_ANY = 1, COLTYPE_BLOB = 2, COLTYPE_INT = 3, COLTYPE_INTEGER = 4,
        COLTYPE_REAL = 5, COLTYPE_TEXT = 6;

    // SQLITE_AFF_* in sqliteInt.h
    public const char AFF_BLOB = 'A', AFF_TEXT = 'B', AFF_NUMERIC = 'C', AFF_INTEGER = 'D', AFF_REAL = 'E';

    public readonly string Name;

    /// <summary>
    /// The declared type as SQLite keeps it: dequoted, or null for a standard type (see <see cref="StandardType"/>)
    /// or no type
    /// </summary>
    public string? Type;

    /// <summary>
    /// eCType: one of the standard type names exactly, or COLTYPE_CUSTOM
    /// </summary>
    public int StandardType;

    public char Affinity;
    public int Flags;
    public int NotNull;

    /// <summary>
    /// The collation from the column's COLLATE, or null for BINARY
    /// </summary>
    public string? Collation;

    /// <summary>
    /// The DEFAULT expression, or the expression of a generated column
    /// </summary>
    public Expr? Default;

    /// <summary>
    /// The DEFAULT's text, as SQLite keeps it in the TK_SPAN node over the expression
    /// </summary>
    public string? DefaultText;

    /// <summary>
    /// True when the column has a DEFAULT or a generated expression (iDflt &gt; 0 in SQLite)
    /// </summary>
    public bool HasDefault;

    public ParsedColumn(string name)
    {
        Name = name;
    }

    public bool Has(int flags) => (Flags & flags) != 0;
}

internal sealed class ParsedIndex
{
    /// <summary>
    /// The indexed columns (aiColumn), in key order
    /// </summary>
    public readonly List<int> Columns = new List<int>();

    /// <summary>
    /// The collation of each key column (azColl)
    /// </summary>
    public readonly List<string> Collations = new List<string>();

    public readonly List<int> SortOrders = new List<int>();

    public int OnError;
    public bool IsPrimaryKey;
}
