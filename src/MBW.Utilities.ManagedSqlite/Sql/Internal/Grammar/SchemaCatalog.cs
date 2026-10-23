using System.Collections.Generic;
using MBW.Utilities.ManagedSqlite.Core.Helpers;

namespace MBW.Utilities.ManagedSqlite.Sql.Internal.Grammar;

/// <summary>
/// The schema as SQLite builds it while it loads sqlite_schema, one row at a time in rowid order: the tables, views
/// and virtual tables by name (tblHash), the indexes by name (idxHash) and the trigger names (trigHash). Each row is
/// checked against the rows before it.
/// </summary>
internal sealed class SchemaCatalog
{
    internal sealed class CatalogTable
    {
        public readonly string Name;

        /// <summary>
        /// The table's columns and keys, or null for a view or a virtual table
        /// </summary>
        public readonly ParsedTable? Table;

        public readonly bool IsView;
        public readonly bool IsVirtual;

        /// <summary>
        /// pIndex: the indexes on the table, automatic and created
        /// </summary>
        public readonly List<CatalogIndex> Indexes = new List<CatalogIndex>();

        public CatalogTable(string name, ParsedTable? table, bool isView, bool isVirtual)
        {
            Name = name;
            Table = table;
            IsView = isView;
            IsVirtual = isVirtual;
        }
    }

    internal sealed class CatalogIndex
    {
        public readonly string Name;
        public readonly CatalogTable Table;

        /// <summary>
        /// tnum: the root page, 0 until the row of an automatic index sets it
        /// </summary>
        public uint RootPage;

        public CatalogIndex(string name, CatalogTable table, uint rootPage)
        {
            Name = name;
            Table = table;
            RootPage = rootPage;
        }
    }

    public readonly Dictionary<string, CatalogTable> Tables = new Dictionary<string, CatalogTable>(AsciiCaseInsensitiveComparer.Instance);
    public readonly Dictionary<string, CatalogIndex> Indexes = new Dictionary<string, CatalogIndex>(AsciiCaseInsensitiveComparer.Instance);
    public readonly HashSet<string> Triggers = new HashSet<string>(AsciiCaseInsensitiveComparer.Instance);

    public SchemaCatalog()
    {
        // The schema table exists before any row is read, as sqlite_master; sqlite3FindTable also finds it as
        // sqlite_schema
        ParsedTable master = new ParsedTable("sqlite_master");
        foreach (string column in new[] { "type", "name", "tbl_name", "rootpage", "sql" })
            master.Columns.Add(new ParsedColumn(column) { Affinity = column == "rootpage" ? ParsedColumn.AFF_INTEGER : ParsedColumn.AFF_TEXT });

        CatalogTable table = new CatalogTable("sqlite_master", master, false, false);
        Tables.Add("sqlite_master", table);
        Tables.Add("sqlite_schema", table);
    }

    /// <summary>
    /// sqlite3EndTable at init: the table goes into tblHash, and with it the indexes its PRIMARY KEY and UNIQUE
    /// constraints made, named sqlite_autoindex_TABLE_N in the order they were made. A WITHOUT ROWID table's PRIMARY
    /// KEY index is the table's own b-tree (convertToWithoutRowidTable sets its tnum); the others get theirs from the
    /// rows that follow.
    /// </summary>
    public void AddTable(ParsedTable table, uint rootPage)
    {
        CatalogTable entry = new CatalogTable(table.Name, table, false, false);
        Tables[table.Name] = entry;

        for (int i = 0; i < table.Indexes.Count; i++)
        {
            ParsedIndex index = table.Indexes[i];
            uint tnum = index.IsPrimaryKey && table.Has(ParsedTable.TF_WithoutRowid) ? rootPage : 0;
            AddIndex(new CatalogIndex($"sqlite_autoindex_{table.Name}_{i + 1}", entry, tnum));
        }
    }

    /// <summary>
    /// sqlite3CreateIndex in build.c, at init: the index goes into idxHash. An automatic index is not looked up first,
    /// so a CREATE INDEX before its table can have taken its name (sqlite_ names are only reserved outside init). SQLite
    /// then finds the name in the hash and fails the schema load with SQLITE_NOMEM; this reader fails it as corrupt.
    ///       p = sqlite3HashInsert(&amp;pIndex->pSchema->idxHash,
    ///                             pIndex->zName, pIndex);
    ///       if( p ){
    ///         assert( p==pIndex );  /* Malloc must have failed */
    ///         sqlite3OomFault(db);
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L4400 (sqlite3CreateIndex)
    /// </summary>
    public void AddIndex(CatalogIndex index)
    {
        if (Indexes.ContainsKey(index.Name))
            Checks.Fail("DDL-10", $"malformed database schema ({index.Table.Name}) - out of memory: index {index.Name} already exists", structure: "schema");

        Indexes.Add(index.Name, index);
        index.Table.Indexes.Add(index);
    }

    /// <summary>
    /// sqlite3IndexHasDuplicateRootPage in prepare.c: another index on the same table has the same root page
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/prepare.c#L62 (sqlite3IndexHasDuplicateRootPage)
    /// </summary>
    public static bool HasDuplicateRootPage(CatalogIndex index)
    {
        foreach (CatalogIndex other in index.Table.Indexes)
        {
            if (other != index && other.RootPage == index.RootPage)
                return true;
        }

        return false;
    }
}
